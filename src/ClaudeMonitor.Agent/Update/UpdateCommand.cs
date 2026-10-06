using System.Globalization;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Update;

/// <summary>
/// "cm-agent update [--check]" and "cm-agent config [auto-update off|check|on]", and the lines `cm-agent status` adds.
/// An explicit update is the person's own decision and ignores the automatic setting; only the daemon's own rounds obey it.
/// </summary>
public static class UpdateCommand
{
    public const string Verb = "update";

    /// <summary>What the daemon passes when it starts an install by itself.</summary>
    public const string AutoFlag = "--auto";

    public static async Task<int> UpdateAsync(string[] args, AgentConfig config, TextWriter stdout, TextWriter stderr, TimeProvider clock,
        HttpMessageHandler? handler = null, IProcessRunner? runner = null, IDaemonControl? daemon = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var checkOnly = args.Contains("--check");
        var auto = args.Contains(AutoFlag);
        var unknown = args.Skip(1).FirstOrDefault(a => a != "--check" && a != AutoFlag);
        if (unknown is not null || (checkOnly && auto))
        {
            await stderr.WriteLineAsync("usage: cm-agent update [--check]");
            return 2;
        }

        var identity = Identity.Load(config);
        if (!identity.Connected)
        {
            await stderr.WriteLineAsync("not connected (cm-agent login --server <url>): the update comes from the server the agent is connected to");
            return 1;
        }

        var log = new AgentLog(config, clock);
        using var http = ApiClient.CreateHttp(identity.Server!, handler);
        using var api = new ApiClient(http, Credentials.For(config), config.ApiCallTimeout);
        var updater = new Updater(config, api, http, runner ?? new SystemProcessRunner(), daemon ?? new DaemonControl(config, log, clock), log, clock);
        var check = await updater.CheckAsync(CancellationToken.None);
        if (check.Offer is null)
        {
            await (check.Refused ? stderr : stdout).WriteLineAsync(check.Refused ? $"update refused: {check.Code}: {check.Message}" : $"{check.Message} ({AgentConfig.Version})");
            return check.Refused ? 1 : 0;
        }

        if (check.BelowMinimum) await stdout.WriteLineAsync($"this version ({AgentConfig.Version}) is below the server's minimum supported version {check.Offer.MinSupported}");
        if (checkOnly)
        {
            await stdout.WriteLineAsync($"update available: {check.Offer.Version} (running {AgentConfig.Version}); install it with: cm-agent update");
            return 0;
        }

        var result = await updater.ApplyAsync(check.Offer, CancellationToken.None);
        await (result.Succeeded ? stdout : stderr).WriteLineAsync(result.Succeeded ? $"updated: {result.Message}" : $"update {result.Code}: {result.Message}");
        return result.Succeeded ? 0 : 1;
    }

    public static async Task<int> ConfigAsync(string[] args, AgentConfig config, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (args.Length == 1)
        {
            await stdout.WriteLineAsync($"auto-update: {config.AutoUpdate}");
            return 0;
        }

        if (args.Length == 3 && args[1] == "auto-update" && UpdateModes.IsValid(args[2]))
        {
            SavedSettings.SaveAutoUpdate(config, args[2]);
            await stdout.WriteLineAsync($"auto-update: {args[2]}");
            return 0;
        }

        await stderr.WriteLineAsync("usage: cm-agent config [auto-update off|check|on]");
        return 2;
    }

    /// <summary>The lines `cm-agent status` shows about updating: the mode on each side, the last verdict, and a rolled-back build.</summary>
    public static IEnumerable<string> Describe(AgentConfig config, LocalStore store)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        var workspace = UpdateModes.Normalize(store.Get(UpdatePolicy.WorkspaceKey));
        yield return $"auto-update: {UpdatePolicy.Effective(config, store)} (this machine: {config.AutoUpdate}, workspace: {workspace})";
        var state = UpdateState.Load(config);
        if (state.Phase == UpdateState.PendingHealth) yield return $"update: {state.From} -> {state.To} is being checked";
        if (state.Available is { } available) yield return $"update available: {available} (cm-agent update)";
        if (state.Result is { } result && result != UpdateCodes.Available)
        {
            var at = DateTimeOffset.TryParse(state.CheckedAt ?? state.InstalledAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
                ? when.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
                : "?";
            yield return $"last update check: {result} ({state.Detail}) {at}";
        }

        if (state.BlockedVersion is { } blocked) yield return $"update {blocked} was rolled back and is not retried by itself";
    }
}
