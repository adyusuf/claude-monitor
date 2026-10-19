using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Capture;
using ClaudeMonitor.Agent.Push;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>What a run of the `claude` program left behind.</summary>
public sealed record ClaudeRun(int ExitCode, string Output, string Error);

/// <summary>Runs `claude` (a seam: tests never start a real one).</summary>
public interface IClaudeCli
{
    Task<ClaudeRun> RunAsync(string claude, IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout, CancellationToken ct);
}

/// <summary>The real `claude`: standard output and error both read, killed when it runs too long.</summary>
public sealed class SystemClaudeCli : IClaudeCli
{
    public async Task<ClaudeRun> RunAsync(string claude, IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        var info = new ProcessStartInfo(claude)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("claude did not start");
        process.StandardInput.Close(); // nothing is ever typed to it
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new ClaudeRun(process.ExitCode, await output, await error);
    }
}

/// <summary>A background session this agent started (ADR-0014). The folder stays here, in the user-only home; the API only ever hears the project key.</summary>
public sealed record ClaudeWorker(string Id, string ProjectKey, string Folder, string Mode, DateTimeOffset StartedAt, DateTimeOffset? StoppedAt = null, string? Reason = null);

/// <summary>What a start or a stop came to: whether it worked, the text for the owner's page, and (for a start) the short id of the session.</summary>
public sealed record ClaudeControlOutcome(bool Succeeded, string Text, string? SessionRef = null);

/// <summary>
/// Starts and stops Claude Code sessions in the background for the machine's owner, from the web (ADR-0014), with Claude Code's OWN commands:
/// `claude --bg` and `claude stop` (measured on 2.1.294). Nothing is built here. It only ever starts a session in a folder that is a project this
/// machine has reported, with a permission mode from a closed list, never in a service agent, never above <see cref="AgentConfig.ClaudeWorkersMax"/> at a
/// time, and it ends only a session it started itself (its id is written to <c>claude-workers.json</c> when it starts): a session the person started
/// is never touched. Claude Code decides whether a folder is trusted; the person has to have accepted that once, by hand, and a refusal is reported as such.
/// The one text from the web is the optional first prompt: capped, defused and wrapped as a message from the web (<see cref="ChannelEnvelopes.BuildFirstPrompt"/>), and never logged;
/// every other argument is a fixed shape of keys and enums.
/// </summary>
public sealed partial class ClaudeControl(AgentConfig config, IClaudeCli cli, TimeProvider clock, Func<ClaudeInstall>? locate = null)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private readonly Lock gate = new();
    private readonly Func<ClaudeInstall> locator = locate ?? (() => ClaudeLocator.Locate(config));

    [GeneratedRegex(@"backgrounded\s*·\s*(?<id>[0-9a-f]{8})", RegexOptions.CultureInvariant)]
    private static partial Regex Backgrounded();

    public async Task<ClaudeControlOutcome> StartAsync(string? projectKey, string? permissionMode, string? prompt, CancellationToken ct, string? reason = null)
    {
        var mode = permissionMode ?? AgentActionKinds.DefaultClaudePermissionMode;
        if (!AgentActionKinds.ClaudePermissionModes.Contains(mode)) return new(false, "refused: that permission mode is not one a remote start may ask for");
        if (projectKey is not { Length: > 0 and <= RunKinds.ProjectKeyMax } || projectKey.Any(char.IsControl)) return new(false, "refused: no project was named");
        if (config.ServiceMode) return new(false, "this agent runs as a service: there is no Claude login or user folder for it to start a session in");
        if (Identity.Peek(config) is { } identity && MachineRoles.IsStrict(identity.ClaimedEnvironment, identity.ClaimedFunction))
        {
            return new(false, "this machine says it is a production or database machine: Claude is never started on it from the web");
        }

        var claude = locator();
        if (claude.Path is null) return new(false, "no `claude` was found on this machine's PATH");
        if (FolderOf(projectKey) is not { } folder) return new(false, "this machine knows no such project (it appears once a session works in it, or its folder is listed with `aipod config projects add`)");
        if (!Directory.Exists(folder)) return new(false, "the project's folder is gone from this machine");
        if (Running().Count >= config.ClaudeWorkersMax) return new(false, $"{config.ClaudeWorkersMax} sessions started from the web are already running here: stop one first");

        List<string> args = ["--bg", "--name", NameFor(projectKey), "--permission-mode", mode];
        if (prompt is { Length: > 0 }) args.Add(ChannelEnvelopes.BuildFirstPrompt(prompt, config.PushContentMax, clock.GetUtcNow()));
        var run = await RunClaudeAsync(claude.Path, args, folder, ct);
        if (run is null) return new(false, "claude could not be started");
        if (run.ExitCode != 0)
        {
            var said = run.Error + run.Output;
            return said.Contains("not trusted", StringComparison.OrdinalIgnoreCase)
                ? new(false, "Claude Code does not trust that folder yet: open Claude there once at the machine and accept the trust prompt, then try again")
                : new(false, $"claude --bg failed (exit {run.ExitCode}): {FirstLine(said)}");
        }

        if (Backgrounded().Match(run.Output) is not { Success: true } match) return new(false, $"claude --bg did not say which session it started: {FirstLine(run.Output)}");
        var id = match.Groups["id"].Value;
        Save(Load().Append(new ClaudeWorker(id, projectKey, folder, mode, clock.GetUtcNow(), Reason: reason)).ToList());
        return new(true, $"started session {id} in the background ({mode} mode); it appears in the session list", id);
    }

    public async Task<ClaudeControlOutcome> StopAsync(string? sessionRef, CancellationToken ct)
    {
        if (!AgentActionKinds.IsSessionRef(sessionRef)) return new(false, "refused: that is not a session id");
        if (Running().All(w => w.Id != sessionRef)) return new(false, "this agent did not start that session (or it is already stopped), so it does not touch it");
        var claude = locator();
        if (claude.Path is null) return new(false, "no `claude` was found on this machine's PATH");
        var run = await RunClaudeAsync(claude.Path, ["stop", sessionRef!], null, ct);
        if (run is null) return new(false, "claude could not be started");
        if (run.ExitCode != 0) return new(false, $"claude stop failed (exit {run.ExitCode}): {FirstLine(run.Error + run.Output)}");
        lock (gate) Save(Load().Select(w => w.Id == sessionRef ? w with { StoppedAt = clock.GetUtcNow() } : w).ToList());
        return new(true, $"stopped session {sessionRef}; its conversation is kept");
    }

    /// <summary>The sessions this agent started and has not stopped.</summary>
    public IReadOnlyList<ClaudeWorker> Running() => Load().Where(w => w.StoppedAt is null).ToList();

    private async Task<ClaudeRun?> RunClaudeAsync(string claude, IReadOnlyList<string> args, string? folder, CancellationToken ct)
    {
        try
        {
            return await cli.RunAsync(claude, args, folder, Wait, ct);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The folder of a project key: the one a session visited, else a listed folder whose project this is.</summary>
    private string? FolderOf(string projectKey) => FolderOf(config, projectKey);

    internal static string? FolderOf(AgentConfig config, string projectKey)
    {
        using (var store = new LocalStore(config.DatabasePath))
        {
            if (store.FolderOf(projectKey) is { } seen) return seen;
        }

        return ProjectFolderList.Saved(config).FirstOrDefault(f => ProjectInfo.Resolve(f)?.Key == projectKey);
    }

    /// <summary>A session name from a project key: its last words, letters and digits only, so nothing but a short label ever reaches `claude --name`.</summary>
    internal static string NameFor(string projectKey)
    {
        var tail = projectKey.Split('/', ':', '\\').LastOrDefault(p => p.Length > 0) ?? "project";
        var clean = new string(tail.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').Take(40).ToArray());
        return "aipod-" + (clean.Length > 0 ? clean : "project");
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no output";
        var safe = new string(line.Where(c => !char.IsControl(c)).ToArray());
        return safe.Length > 200 ? safe[..200] : safe;
    }

    private List<ClaudeWorker> Load()
    {
        lock (gate)
        {
            try
            {
                return File.Exists(config.ClaudeWorkersPath) ? JsonSerializer.Deserialize<List<ClaudeWorker>>(File.ReadAllText(config.ClaudeWorkersPath, Encoding.UTF8)) ?? [] : [];
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                return []; // an unreadable file counts as no sessions of ours: nothing is stopped on a guess
            }
        }
    }

    private void Save(List<ClaudeWorker> workers)
    {
        lock (gate)
        {
            config.EnsureHome();
            var temp = config.ClaudeWorkersPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(workers.TakeLast(200).ToList()), Encoding.UTF8);
            File.Move(temp, config.ClaudeWorkersPath, overwrite: true);
        }
    }
}
