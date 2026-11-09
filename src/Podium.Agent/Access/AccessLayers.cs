using System.Text.Json;
using Microsoft.Data.Sqlite;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Exec;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Access;

/// <summary>
/// The layers a run is checked against (ADR-0020 §4, maintainer amendment 1), always the ceiling first. A run whose repository a stored policy covers:
/// the ceiling and that policy (the default instead when it does not apply: invalid, unsigned where signatures are required, cleared). Otherwise, when the
/// workspace enforces access policies or the machine is strict (production or database): the ceiling and the default (the project's folders, argv only).
/// Otherwise the ceiling only, as before. Anything that cannot be read gives the ceiling and the default with no folder, never "no limit".
/// </summary>
public sealed class AccessLayers(AgentConfig config)
{
    /// <summary>Whether the workspace's machine list (the API's view) says this machine is production or database: "true", "false", or unread.</summary>
    public const string StrictKey = "access.self_strict";

    public IReadOnlyList<AccessLayer> For(RunMessage run, ExecPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(run);
        return For(run.ProjectKey, policy, run);
    }

    /// <summary>The layers for a repository key (a session's project, or a run's when <paramref name="run"/> is given).</summary>
    public IReadOnlyList<AccessLayer> For(string? projectKey, ExecPolicy policy, RunMessage? run = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var ceiling = AccessCeiling.Layer(policy);
        var key = string.IsNullOrEmpty(projectKey) ? null : projectKey;
        try
        {
            using var store = new LocalStore(config.DatabasePath);
            var judged = AccessPolicyVerifier.Load(config, store);
            var covering = key is null ? [] : judged.Where(p => p.Keys.Contains(key, StringComparer.Ordinal)).ToList();
            // A stored policy that names no repository it could be for (unreadable) cannot be placed: every run then gets the default.
            var defaultApplies = WorkspaceSettings.Get(config, store, AccessPolicySync.EnforceKey) == "true" || IsStrict(run, policy, store)
                || judged.Any(p => p.Keys.Count == 0);
            var folders = key is null ? [] : AccessFolders.For(key, policy.ProjectFolders, store, AgentConfig.Os);
            return Compose(ceiling, covering, defaultApplies, folders, AgentConfig.Os);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return [ceiling, AccessPolicyMath.Default([])];
        }
    }

    /// <summary>The ceiling, then every covering policy that applies, and the default when one covering policy does not apply or none covers and the default applies.</summary>
    public static IReadOnlyList<AccessLayer> Compose(AccessLayer ceiling, IReadOnlyList<AppliedAccessPolicy> covering, bool defaultApplies,
        IReadOnlyList<string> projectFolders, string os)
    {
        ArgumentNullException.ThrowIfNull(covering);
        if (covering.Count == 0) return defaultApplies ? [ceiling, AccessPolicyMath.Default(projectFolders)] : [ceiling];
        List<AccessLayer> layers = [ceiling];
        foreach (var policy in covering)
        {
            if (policy.Document is not { } document || AccessPolicyRules.Section(document, os) is not { } rules) continue;
            layers.Add(AccessPolicyMath.Policy(policy.Version, rules, document.IncludeProjectFolder, projectFolders, document.Shell));
        }

        if (covering.Any(p => p.Document is null) || layers.Count == 1) layers.Add(AccessPolicyMath.Default(projectFolders));
        return layers;
    }

    /// <summary>
    /// A strict machine (ADR-0008): the local claim, the role the API froze into a script run, or the role the workspace's machine list last gave this
    /// machine. A role value this build does not know counts as strict.
    /// </summary>
    public static bool IsStrict(RunMessage? run, ExecPolicy policy, LocalStore store)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(store);
        return MachineRoles.IsStrict(policy.ClaimedEnvironment, policy.ClaimedFunction) || MachineRoles.IsStrict(run?.TargetEnvironment, run?.TargetFunction)
            || !MachineRoles.IsEnvironment(run?.TargetEnvironment) || !MachineRoles.IsFunction(run?.TargetFunction) || store.Get(StrictKey) == "true";
    }

    /// <summary>Keeps what the workspace's machine list says about this machine's role, for the runs that do not carry it.</summary>
    public static void RememberRole(LocalStore store, IReadOnlyList<MachineView> machines, Guid? self)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(machines);
        if (self is null || machines.FirstOrDefault(m => m.AgentId == self) is not { } me) return;
        var strict = MachineRoles.IsStrict(me.EffectiveEnvironment, me.EffectiveFunction) || MachineRoles.IsStrict(me.RoleEnvironment, me.RoleFunction)
            || me.RoleEnvironments?.Contains(MachineEnvironments.Production) == true || me.RoleFunctions?.Contains(MachineFunctions.Database) == true
            || !MachineRoles.IsEnvironment(me.EffectiveEnvironment) || !MachineRoles.IsFunction(me.EffectiveFunction);
        store.Set(StrictKey, strict ? "true" : "false");
    }
}
