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

    public async Task<ClaudeOutcome> RunAsync(CancellationToken ct)
    {
        ResolveInterruptedCountdown();
        if (!ClaudePolicy.Allowed(config, store)) return new(ClaudeCodes.Disabled, "not allowed by this machine and the workspace");
        var state = ClaudeUpdateState.Load(config);
        if (Later(state.NextAt) is { } wait) return new(ClaudeCodes.NotDue, $"next attempt after {wait:O}");
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

        var before = Version(install.Path!);
        var (exit, _) = await Task.Run(() => runner.Run(install.Path!, ["update"], config.ClaudeUpdateTimeout), ct);
        var after = Version(install.Path!);
        if (exit != 0) return Record(ClaudeCodes.Failed, $"`claude update` exited with {exit}", before, after, next: config.UpdateRetryAfter);
        var changed = after is not null && after != before;
        return Record(changed ? ClaudeCodes.Updated : ClaudeCodes.Unchanged,
            changed ? $"Claude Code {before ?? "?"} -> {after}; running sessions keep their version until they are restarted" : $"Claude Code is up to date ({after ?? "version unknown"})",
            before, after, next: config.ClaudeUpdateEvery);
    }

    /// <summary>A countdown is only true while an attempt holds the lock; found with none alive (the daemon was stopped) it is cleared, so `status` never promises an update that will not come.</summary>
    private void ResolveInterruptedCountdown()
    {
        if (ClaudeUpdateState.Load(config).CountdownUntil is null) return;
        using var attempt = DaemonHost.TryLock(config.ClaudeUpdateLockPath);
        if (attempt is null) return;
        ClaudeUpdateState.Change(config, s => s with { CountdownUntil = null, Result = "interrupted", Detail = "the countdown ended without an update (the agent was stopped)" });
    }

    /// <summary>Announces the update and waits; stops early when cancelled, switched off, or a session is no longer idle.</summary>
    private async Task<ClaudeOutcome?> CountdownAsync(ClaudeInstall install, CancellationToken ct)
    {
        TryDelete(config.ClaudeCancelPath);
        var until = clock.GetUtcNow() + config.ClaudeCountdown;
        ClaudeUpdateState.Change(config, s => s with { CountdownUntil = until.ToString("O", CultureInfo.InvariantCulture), Result = "countdown", Detail = "Claude Code will be updated when the countdown ends" });
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

    private bool Idle(out string why)
    {
        var (idle, reason) = ClaudeSessions.AllIdle(ClaudeSessions.Read(config, alive), clock.GetUtcNow(), config.ClaudeIdleFor);
        why = reason;
        return idle;
    }

    /// <summary>Not idle: remembered for `status`, written to the log only when it changes (the poll repeats every few minutes).</summary>
    private ClaudeOutcome Waiting(ClaudeUpdateState state, string why)
    {
        var detail = $"waiting: {why}";
        if (state.Result != ClaudeCodes.Waiting || state.Detail != detail) log.Write($"claude update {detail}");
        ClaudeUpdateState.Change(config, s => s with { CheckedAt = Now(), Result = ClaudeCodes.Waiting, Detail = detail, CountdownUntil = null });
        return new(ClaudeCodes.Waiting, detail);
    }

    private ClaudeOutcome Record(string code, string detail, string? before = null, string? after = null, TimeSpan? next = null)
    {
        var due = next is { } n && n > TimeSpan.Zero ? (clock.GetUtcNow() + n).ToString("O", CultureInfo.InvariantCulture) : null;
        ClaudeUpdateState.Change(config, s => s with
        {
            CheckedAt = Now(),
            Result = code,
            Detail = detail,
            VersionBefore = before ?? s.VersionBefore,
            VersionAfter = after ?? s.VersionAfter,
            NextAt = due,
            CountdownUntil = null,
        });
        log.Write($"claude update {code}: {detail}");
        return new(code, detail);
    }

    private string? Version(string claude)
    {
        var (exit, output) = runner.Run(claude, ["--version"], VersionTimeout);
        return exit == 0 && VersionPattern().Match(output) is { Success: true } m ? m.Value : null;
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
