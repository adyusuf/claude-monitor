using System.Globalization;
using ClaudeMonitor.Agent.Approvals;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Update;

/// <summary>
/// "aipod update [--check]" and "aipod config [auto-update off|check|on]", and the lines `aipod status` adds.
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
            await stderr.WriteLineAsync(Strings.T(Msg.UpdateUsage));
            return 2;
        }

        var identity = Identity.Load(config);
        if (!identity.Connected)
        {
            await stderr.WriteLineAsync(Strings.T(Msg.UpdateNotConnected));
            return 1;
        }

        var log = new AgentLog(config, clock);
        using var http = ApiClient.CreateHttp(identity.Server!, handler);
        using var api = new ApiClient(http, Credentials.For(config), config.ApiCallTimeout);
        var updater = new Updater(config, api, http, runner ?? new SystemProcessRunner(), daemon ?? new DaemonControl(config, log, clock), log, clock);
        var check = await updater.CheckAsync(CancellationToken.None);
        if (check.Offer is null)
        {
            await (check.Refused ? stderr : stdout).WriteLineAsync(check.Refused ? Strings.T(Msg.UpdateRefused, check.Code, check.Message) : Strings.T(Msg.UpdateCurrent, check.Message, AgentConfig.Version));
            return check.Refused ? 1 : 0;
        }

        if (check.BelowMinimum) await stdout.WriteLineAsync(Strings.T(Msg.UpdateBelowMinimum, AgentConfig.Version, check.Offer.MinSupported));
        if (checkOnly)
        {
            await stdout.WriteLineAsync(Strings.T(Msg.UpdateAvailable, check.Offer.Version, AgentConfig.Version));
            return 0;
        }

        var result = await updater.ApplyAsync(check.Offer, CancellationToken.None);
        await (result.Succeeded ? stdout : stderr).WriteLineAsync(result.Succeeded ? Strings.T(Msg.UpdateDone, result.Message) : Strings.T(Msg.UpdateFailed, result.Code, result.Message));
        return result.Succeeded ? 0 : 1;
    }

    public static async Task<int> ConfigAsync(string[] args, AgentConfig config, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (args.Length == 1)
        {
            await stdout.WriteLineAsync(Strings.T(Msg.ConfigAutoUpdate, config.AutoUpdate));
            return 0;
        }

        if (args.Length == 3 && args[1] == "claude-update" && args[2] is "on" or "off")
        {
            SavedSettings.SaveClaudeUpdate(config, args[2] == "on");
            await stdout.WriteLineAsync(Strings.T(Msg.ConfigClaudeUpdate, args[2]));
            return 0;
        }

        if (args.Length == 3 && args[1] == "auto-update" && UpdateModes.IsValid(args[2]))
        {
            SavedSettings.SaveAutoUpdate(config, args[2]);
            await stdout.WriteLineAsync(Strings.T(Msg.ConfigAutoUpdate, args[2]));
            return 0;
        }

        if (args.Length == 3 && args[1] == "push" && (args[2] == "off" || PushTransports.Enables(args[2])))
        {
            SavedSettings.SavePush(config, args[2] != "off", args[2] == "off" ? null : PushTransports.Normalize(args[2]));
            await stdout.WriteLineAsync(args[2] == "off" ? Strings.T(Msg.ConfigPushOff) : Strings.T(Msg.ConfigPushOn, PushTransports.Normalize(args[2])));
            return 0;
        }

        if (args.Length == 3 && args[1] == "push-format" && InboxFormats.IsValid(args[2]))
        {
            SavedSettings.SaveInboxFormat(config, args[2]);
            await stdout.WriteLineAsync(Strings.T(args[2] == InboxFormats.Brief ? Msg.ConfigPushFormatShort : Msg.ConfigPushFormatFull));
            return 0;
        }

        if (args.Length == 4 && args[1] == "role")
        {
            var environment = args[2] == "none" ? null : args[2];
            var function = args[3] == "none" ? null : args[3];
            if (!MachineRoles.IsEnvironment(environment) || !MachineRoles.IsFunction(function))
            {
                await stderr.WriteLineAsync(Strings.T(Msg.ConfigRoleUsage, string.Join('|', MachineEnvironments.All), string.Join('|', MachineFunctions.All)));
                return 2;
            }

            SavedSettings.SaveRole(config, environment, function);
            await stdout.WriteLineAsync(Strings.T(Msg.ConfigRoleClaim, MachineRoles.Code(environment, function)));
            return 0;
        }

        if (args.Length == 3 && args[1] == "approvals" && args[2] is "required" or "off")
        {
            ApproversStore.SetRequired(config, args[2] == "required");
            var policy = ApproversStore.Load(config);
            await stdout.WriteLineAsync(Strings.T(Msg.ConfigApprovals, args[2], policy.Keys.Count)
                + (args[2] == "required" && policy.Keys.Count == 0 ? Strings.T(Msg.ConfigApprovalsNothing) : ""));
            return 0;
        }

        if (args.Length >= 2 && args[1] == "projects")
        {
            return await ProjectsAsync(args, config, stdout, stderr);
        }

        await stderr.WriteLineAsync(Strings.T(Msg.ConfigUsage));
        return 2;
    }

    /// <summary>
    /// `aipod config projects add|remove|list [folder]` (ADR-0008 §3a′): the project folders this machine reports facts about when no session
    /// visits them. A service agent reads its list from the admin-owned policy file instead; this verb is for an interactive or standalone agent.
    /// </summary>
    private static async Task<int> ProjectsAsync(string[] args, AgentConfig config, TextWriter stdout, TextWriter stderr)
    {
        if (config.ServiceMode)
        {
            await stderr.WriteLineAsync(Strings.T(Msg.ProjectsService));
            return 2;
        }

        if (args.Length == 3 && args[2] == "list")
        {
            foreach (var folder in ProjectFolderList.Saved(config)) await stdout.WriteLineAsync(folder);
            return 0;
        }

        if (args.Length == 4 && args[2] == "add")
        {
            if (ProjectFolderList.Add(config, args[3]))
            {
                await stdout.WriteLineAsync(Strings.T(Msg.ProjectsListed));
                return 0;
            }

            await stderr.WriteLineAsync(Strings.T(Msg.ProjectsNotListed, ProjectFolderList.Max));
            return 2;
        }

        if (args.Length == 4 && args[2] == "remove")
        {
            await (ProjectFolderList.Remove(config, args[3]) ? stdout.WriteLineAsync(Strings.T(Msg.ProjectsRemoved)) : stderr.WriteLineAsync(Strings.T(Msg.ProjectsNotInList)));
            return 0;
        }

        await stderr.WriteLineAsync(Strings.T(Msg.ProjectsUsage));
        return 2;
    }

    /// <summary>The lines `aipod status` shows about updating: the mode on each side, the last verdict, and a rolled-back build.</summary>
    public static IEnumerable<string> Describe(AgentConfig config, LocalStore store)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        var answer = Daemon.WorkspaceSettings.Get(config, store, UpdatePolicy.WorkspaceKey);
        var workspace = answer is null ? Daemon.WorkspaceSettings.Unread : UpdateModes.Normalize(answer);
        yield return Strings.T(Msg.UpAuto, UpdatePolicy.Effective(config, store), config.AutoUpdate, workspace);
        var state = UpdateState.Load(config);
        if (state.Phase == UpdateState.PendingHealth) yield return Strings.T(Msg.UpChecking, state.From, state.To);
        if (state.Available is { } available) yield return Strings.T(Msg.UpAvailable, available);
        if (state.Result is { } result && result != UpdateCodes.Available)
        {
            var at = DateTimeOffset.TryParse(state.CheckedAt ?? state.InstalledAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
                ? when.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
                : "?";
            yield return Strings.T(Msg.UpLastCheck, result, state.Detail, at);
        }

        if (state.BlockedVersion is { } blocked) yield return Strings.T(Msg.UpRolledBack, blocked);
    }
}
