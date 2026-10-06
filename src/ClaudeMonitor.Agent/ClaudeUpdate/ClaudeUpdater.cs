using System.Globalization;
using System.Text.RegularExpressions;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;

namespace ClaudeMonitor.Agent.ClaudeUpdate;

public sealed record ClaudeOutcome(string Code, string Detail);

/// <summary>
/// Updates Claude Code with its own command (`claude update`) and nothing else: the agent never downloads or replaces a Claude
/// binary. It happens only when ALL of these hold: this machine and the workspace both allow it; the install is one that updates
/// itself (npm or native CLI); every live session is idle and has been for a while; and a countdown, announced to the person,
/// ran out without `cm-agent claude-update cancel`. Sessions are never ended or restarted: they keep running the version they
/// started with until they are restarted by their owner. Every attempt, its versions and its result go to agent.log and state.
/// </summary>
public sealed partial class ClaudeUpdater(AgentConfig config, LocalStore store, IProcessRunner runner, IUserNotifier notifier, AgentLog log,
    TimeProvider clock, Func<int, bool>? alive = null)
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(30);

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionPattern();

    /// <summary>Set when a result could not be saved: no attempt starts in this process before then, as the file cannot say so.</summary>
    private DateTimeOffset? heldUntil;

    public async Task<ClaudeOutcome> RunAsync(CancellationToken ct)
    {
        ResolveInterruptedCountdown();
        if (!ClaudePolicy.Allowed(config, store)) return new(ClaudeCodes.Disabled, "not allowed by this machine and the workspace");
        var state = ClaudeUpdateState.Load(config);
        if (Later(state.NextAt) is { } wait) return new(ClaudeCodes.NotDue, $"next attempt after {wait:O}");
        if (heldUntil is { } held && held > clock.GetUtcNow()) return new(ClaudeCodes.NotDue, $"next attempt after {held:O} (the last result could not be saved)");
        config.EnsureHome();
        using var guard = DaemonHost.TryLock(config.ClaudeUpdateLockPath);
        if (guard is null) return new(ClaudeCodes.Busy, "an attempt is already running");

        var install = ClaudeLocator.Locate(config);
        if (!install.Updatable)
        {
            var code = install.Kind == ClaudeKind.NotFound ? ClaudeCodes.NoClaude : ClaudeCodes.Unsupported;
            return Record(code, install.Detail, next: config.ClaudeUpdateEvery);
        }

        if (!Idle(out var why)) return Waiting(state, why);
        if (await CountdownAsync(install, ct) is { } stopped) return stopped;

        var before = Version(install.Path!, ct);
        if (ct.IsCancellationRequested) return Record(ClaudeCodes.Interrupted, "the agent stopped before `claude update` started", before, next: config.UpdateRetryAfter);
        // The daemon's stop (its own, or one a self-update asks for) kills `claude update`: see ADR-0006 rule 7.
        var run = await Task.Run(() => runner.Run(install.Path!, ["update"], config.ClaudeUpdateTimeout, ct), CancellationToken.None);
        if (run.End == ProcessEnd.Cancelled)
        {
            return Record(ClaudeCodes.Interrupted, $"the agent stopped while `claude update` ran, so it was ended{Killed(run)}; if `claude` no longer starts, run `claude update` by hand",
                before, next: config.UpdateRetryAfter);
        }

        var after = Version(install.Path!, ct);
        if (Failure(run) is { } failure) return Failed(failure, before, after);
        if (after is null && ct.IsCancellationRequested)
        {
            // the update ran to its end but the version check was cut short: "up to date" would be a guess
            return Record(ClaudeCodes.Interrupted, "`claude update` ended, but the agent stopped before it read the new version; the next attempt reads it",
                before, next: config.UpdateRetryAfter);
        }

        var changed = after is not null && after != before;
        return Record(changed ? ClaudeCodes.Updated : ClaudeCodes.Unchanged,
            changed ? $"Claude Code {before ?? "?"} -> {after}; running sessions keep their version until they are restarted" : $"Claude Code is up to date ({after ?? "version unknown"})",
            before, after, next: config.ClaudeUpdateEvery, failures: 0);
    }

    /// <summary>Why the run is a failure, or null when it exited 0. Only how it ended is told, never what it printed.</summary>
    private string? Failure(ProcessResult run) => run.End switch
    {
        ProcessEnd.Exited when run.ExitCode == 0 => null,
        ProcessEnd.Exited => $"`claude update` exited with {run.ExitCode}",
        ProcessEnd.TimedOut => $"`claude update` did not end within {config.ClaudeUpdateTimeout.TotalMinutes:0} min and was ended{Killed(run)}",
        ProcessEnd.NotStarted => $"`claude update` could not be started{(run.Error is { } e ? $" ({e})" : "")}",
        _ => $"`claude update` ended in a way this agent does not know ({run.End})",
    };

    private static string Killed(ProcessResult run) => run.Error is { } e ? $" (ending it failed: {e})" : "";

    /// <summary>A failure is retried after the retry delay; after several in a row only after the usual time, so a lasting fault is not tried every hour.</summary>
    private ClaudeOutcome Failed(string why, string? before, string? after)
    {
        var failures = ClaudeUpdateState.Load(config).Failures + 1;
        var backOff = failures >= config.ClaudeFailuresBeforeBackoff;
        var detail = backOff ? $"{why}; {failures} failures in a row, so the next attempt waits {config.ClaudeUpdateEvery.TotalHours:0} h" : why;
        return Record(ClaudeCodes.Failed, detail, before, after, next: backOff ? config.ClaudeUpdateEvery : config.UpdateRetryAfter, failures: failures);
    }

    /// <summary>A countdown is only true while an attempt holds the lock; found with none alive (the daemon was stopped) it is cleared, so `status` never promises an update that will not come.</summary>
    private void ResolveInterruptedCountdown()
    {
        if (ClaudeUpdateState.Load(config).CountdownUntil is null) return;
        using var attempt = DaemonHost.TryLock(config.ClaudeUpdateLockPath);
        if (attempt is null) return;
        ClaudeUpdateState.Change(config, s => s with { CountdownUntil = null, Result = ClaudeCodes.Interrupted, Detail = "the countdown ended without an update (the agent was stopped)" }, log);
    }

    /// <summary>Announces the update and waits; stops early when cancelled, switched off, or a session is no longer idle.</summary>
    private async Task<ClaudeOutcome?> CountdownAsync(ClaudeInstall install, CancellationToken ct)
    {
        TryDelete(config.ClaudeCancelPath);
        var until = clock.GetUtcNow() + config.ClaudeCountdown;
        if (!ClaudeUpdateState.Change(config, s => s with { CountdownUntil = until.ToString("O", CultureInfo.InvariantCulture), Result = ClaudeCodes.Countdown, Detail = "Claude Code will be updated when the countdown ends" }, log))
        {
            // `cm-agent claude-update cancel` reads the countdown from the file: one it cannot see must not run
            return Record(ClaudeCodes.NotSaved, "the countdown could not be saved, so it could not be cancelled: nothing was run", next: config.UpdateRetryAfter);
        }

        var minutes = Math.Ceiling(config.ClaudeCountdown.TotalMinutes);
        var text = $"Claude Code will be updated in {minutes:0} min. Cancel with: cm-agent claude-update cancel";
        log.Write($"claude update: {text} ({install.Detail}, {install.Path})");
        if (!notifier.Notify(text)) log.Write("claude update: no desktop notification on this system; the log and `cm-agent status` carry the notice");
        while (true)
        {
            // Checked once more at the very end too: a cancel, a busy session or a withdrawn consent in the last second counts.
            if (File.Exists(config.ClaudeCancelPath))
            {
                TryDelete(config.ClaudeCancelPath);
                return Record(ClaudeCodes.Cancelled, "cancelled during the countdown", next: config.ClaudeSnooze);
            }

            if (!ClaudePolicy.Allowed(config, store)) return Record(ClaudeCodes.Disabled, "switched off during the countdown", next: config.ClaudeUpdateEvery);
            if (!Idle(out var why)) return Record(ClaudeCodes.Waiting, $"stopped: {why}", next: TimeSpan.Zero);
            if (clock.GetUtcNow() >= until) return null;
            await Task.Delay(config.ClaudeCountdownPoll, clock, ct);
        }
    }

    /// <summary>Every config folder a session was seen under lately (the daemon's own included) must be all-idle.</summary>
    private bool Idle(out string why)
    {
        var now = clock.GetUtcNow();
        var (idle, reason) = ClaudeSessions.AllIdle(ClaudeSessions.ConfigDirs(config, store, now, config.ClaudeUpdateEvery), alive, now, config.ClaudeIdleFor);
        why = reason;
        return idle;
    }

    /// <summary>Not idle: remembered for `status`, written to the log only when it changes (the poll repeats every few minutes).</summary>
    private ClaudeOutcome Waiting(ClaudeUpdateState state, string why)
    {
        var detail = $"waiting: {why}";
        if (state.Result != ClaudeCodes.Waiting || state.Detail != detail) log.Write($"claude update {detail}");
        ClaudeUpdateState.Change(config, s => s with { CheckedAt = Now(), Result = ClaudeCodes.Waiting, Detail = detail, CountdownUntil = null }, log);
        return new(ClaudeCodes.Waiting, detail);
    }

    /// <summary>Remembers the result; when it cannot be saved, no attempt starts in this process before the usual time (or a longer <paramref name="next"/>).</summary>
    private ClaudeOutcome Record(string code, string detail, string? before = null, string? after = null, TimeSpan? next = null, int? failures = null)
    {
        var due = next is { } n && n > TimeSpan.Zero ? (clock.GetUtcNow() + n).ToString("O", CultureInfo.InvariantCulture) : null;
        var saved = ClaudeUpdateState.Change(config, s => s with
        {
            CheckedAt = Now(),
            Result = code,
            Detail = detail,
            VersionBefore = before ?? s.VersionBefore,
            VersionAfter = after ?? s.VersionAfter,
            NextAt = due,
            CountdownUntil = null,
            Failures = failures ?? s.Failures,
        }, log);
        log.Write($"claude update {code}: {detail}");
        if (!saved)
        {
            heldUntil = clock.GetUtcNow() + (next is { } longer && longer > config.ClaudeUpdateEvery ? longer : config.ClaudeUpdateEvery);
            log.Write($"claude update: the result could not be saved; no attempt starts before {heldUntil:O} while this agent runs");
        }

        return new(code, detail);
    }

    private string? Version(string claude, CancellationToken ct)
    {
        var run = runner.Run(claude, ["--version"], VersionTimeout, ct);
        return run is { End: ProcessEnd.Exited, ExitCode: 0 } && VersionPattern().Match(run.Output) is { Success: true } m ? m.Value : null;
    }

    private DateTimeOffset? Later(string? iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) && at > clock.GetUtcNow() ? at : null;

    private string Now() => clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
