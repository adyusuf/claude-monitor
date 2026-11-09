using System.Text.Json;
using Microsoft.Data.Sqlite;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Exec;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Access;

/// <summary>
/// The layers a run is checked against (ADR-0020 §4, maintainer amendment 1), always the ceiling first. The repositories are the run's label and the
/// checkouts its real paths lie in (<see cref="AccessRunKeys"/>); every stored policy covering one of them applies (the default instead when one does
/// not apply: invalid, unsigned where signatures are required, cleared). With none covering, when the workspace enforces access policies, the machine
/// is strict (production or database), or the delivered set was cut at its cap: the ceiling and the default (the project's folders, argv only).
/// Otherwise the ceiling only, as before. Anything that cannot be read gives the ceiling and the default with no folder, never "no limit".
/// </summary>
public sealed class AccessLayers(AgentConfig config)
{
    /// <summary>Whether the workspace's machine list (the API's view) says this machine is production or database: "true", "false", or unread.</summary>
    public const string StrictKey = "access.self_strict";

    /// <summary>The layers for a run on its real working directory and paths, or the refusal when the repositories it touches cannot be judged.</summary>
    public (IReadOnlyList<AccessLayer> Layers, AccessRefusal? Refusal) For(RunMessage run, ExecPolicy policy, string? cwd, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(run);
        var (keys, refusal) = AccessRunKeys.Of(run.ProjectKey, cwd, paths);
        return refusal is not null ? ([], refusal) : (For(keys, policy, run), null);
    }

    /// <summary>The layers for these repository keys (a session's, or a run's when <paramref name="run"/> is given).</summary>
    public IReadOnlyList<AccessLayer> For(IReadOnlyList<string> keys, ExecPolicy policy, RunMessage? run = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(policy);
        var ceiling = AccessCeiling.Layer(policy);
        try
        {
            using var store = new LocalStore(config.DatabasePath);
            var judged = AccessPolicyVerifier.Load(config, store);
            var covering = Covering(judged, keys);
            // A stored policy that names no repository it could be for (unreadable), or a delivery cut at its cap, cannot be placed: every run gets the default.
            var defaultApplies = WorkspaceSettings.Get(config, store, AccessPolicySync.EnforceKey) == "true" || IsStrict(run, policy, store)
                || judged.Any(p => p.Keys.Count == 0) || store.Get(LocalStore.AccessTruncatedKey) == "true";
            IReadOnlyList<string> FoldersOf(IEnumerable<string> of) =>
                [.. of.SelectMany(key => AccessFolders.For(key, policy.ProjectFolders, store, AgentConfig.Os)).Distinct(StringComparer.Ordinal)];
            return Compose(ceiling, covering, defaultApplies, FoldersOf, keys, AgentConfig.Os);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return [ceiling, AccessPolicyMath.Default([])];
        }
    }

    /// <summary>
    /// The stored policies that cover one of these keys. A policy that does not apply (a tombstone, an invalid or ignored message) does not cover a key
    /// that an applying policy of another project covers: a key moved to another project's live policy is held to that one, not to the default.
    /// </summary>
    public static List<AppliedAccessPolicy> Covering(IReadOnlyList<AppliedAccessPolicy> judged, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(judged);
        ArgumentNullException.ThrowIfNull(keys);
        var applying = judged.Where(p => p.Document is not null).ToList();
        bool Holds(AppliedAccessPolicy p, string key) =>
            p.Document is not null || !applying.Any(other => other.ProjectId != p.ProjectId && other.Keys.Contains(key, RepoKeys.Comparer));
        return [.. judged.Where(p => p.Keys.Any(k => keys.Contains(k, RepoKeys.Comparer) && Holds(p, k)))];
    }

    /// <summary>
    /// The ceiling, then every covering policy that applies (with its own repositories' folders), and the default (the run's repositories' folders) when
    /// one covering policy does not apply, or none covers and the default applies.
    /// </summary>
    public static IReadOnlyList<AccessLayer> Compose(AccessLayer ceiling, IReadOnlyList<AppliedAccessPolicy> covering, bool defaultApplies,
        Func<IEnumerable<string>, IReadOnlyList<string>> foldersOf, IReadOnlyList<string> keys, string os)
    {
        ArgumentNullException.ThrowIfNull(covering);
        ArgumentNullException.ThrowIfNull(foldersOf);
        if (covering.Count == 0) return defaultApplies ? [ceiling, AccessPolicyMath.Default(foldersOf(keys))] : [ceiling];
        List<AccessLayer> layers = [ceiling];
        foreach (var policy in covering)
        {
            if (policy.Document is not { } document || AccessPolicyRules.Section(document, os) is not { } rules) continue;
            layers.Add(AccessPolicyMath.Policy(policy.Version, rules, document.IncludeProjectFolder, foldersOf(policy.Keys), document.Shell));
        }

        if (covering.Any(p => p.Document is null) || layers.Count == 1) layers.Add(AccessPolicyMath.Default(foldersOf(keys)));
        return layers;
    }

    /// <summary>
    /// A strict machine (ADR-0008); unsure counts as strict. For a run: what the API held at delivery (TargetStrict; none from an older API counts as
    /// strict), the local claim, the role the API froze into a script run (a value this build does not know counts as strict), or the machine list's
    /// "strict". Without a run (a session's tools, the reports): the local claim, or the machine list's answer, where an unread one counts as strict.
    /// </summary>
    public static bool IsStrict(RunMessage? run, ExecPolicy policy, LocalStore store)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(store);
        if (MachineRoles.IsStrict(policy.ClaimedEnvironment, policy.ClaimedFunction)) return true;
        if (run is null) return store.Get(StrictKey) != "false";
        return run.TargetStrict != false || MachineRoles.IsStrict(run.TargetEnvironment, run.TargetFunction)
            || !MachineRoles.IsEnvironment(run.TargetEnvironment) || !MachineRoles.IsFunction(run.TargetFunction) || store.Get(StrictKey) == "true";
    }

    /// <summary>Keeps what the workspace's machine list says about this machine's role: an extra "strict" for runs, the answer for the session's tools.</summary>
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
