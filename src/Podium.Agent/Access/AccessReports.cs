using System.Text.Json;
using Microsoft.Data.Sqlite;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Exec;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Access;

/// <summary>
/// What this machine says about access (ADR-0020 §4): the report in its profile (the ceiling and the state of each policy; of the project folders only
/// how many there are), the `aipod status` line and the policy lines of `aipod config access show`. Paths of the ceiling leave the machine only in the
/// profile, which the web shows to the machine's owner and the workspace's admins.
/// </summary>
public static class AccessReports
{
    /// <summary>At most this many policies are reported, as many as the API delivers.</summary>
    public const int MaxPolicies = 200;

    public static AccessReport Report(AgentConfig config, LocalStore store, ExecPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        var judged = AccessPolicyVerifier.Load(config, store).Take(MaxPolicies);
        return new AccessReport(AccessCeiling.Report(policy),
            [.. judged.Select(p => new AccessAppliedReport(p.ProjectId, p.Version, p.State, p.IsSigned, p.KeyId, FolderCount(p, policy, store)))]);
    }

    /// <summary>One line for `aipod status`: how many entries the ceiling has, how many policies apply, and what a run without one gets.</summary>
    public static string Describe(AgentConfig config, LocalStore store, ExecPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        var ceiling = AccessCeiling.Of(policy);
        var judged = AccessPolicyVerifier.Load(config, store);
        var applied = judged.Count(p => p.Document is not null);
        return Strings.T(Msg.StAccess, ceiling.AllowedRoots.Count, ceiling.DeniedRoots.Count, ceiling.Executables.Count, applied, judged.Count - applied,
            Strings.T(DefaultApplies(config, store, policy) ? Msg.AccessDefaultApplies : Msg.AccessCeilingOnly));
    }

    /// <summary>The policy lines of `aipod config access show`: one per repository a policy covers, with its version, state, signature and counts.</summary>
    public static IEnumerable<string> DescribePolicies(AgentConfig config, ExecPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(policy);
        if (!File.Exists(config.DatabasePath)) return [Strings.T(Msg.AccessShowNoStore, config.Home)];
        try
        {
            using var store = new LocalStore(config.DatabasePath);
            var judged = AccessPolicyVerifier.Load(config, store);
            List<string> lines = [Strings.T(Msg.AccessShowPolicies, judged.Count)];
            foreach (var p in judged)
            {
                var rules = p.Document is { } document ? AccessPolicyRules.Section(document, AgentConfig.Os) : null;
                foreach (var key in p.Keys.DefaultIfEmpty(p.ProjectId.ToString("D")))
                {
                    lines.Add(Strings.T(Msg.AccessShowPolicy, key, p.Version, p.State, Strings.T(p.IsSigned ? Msg.AccessSigned : Msg.AccessUnsigned),
                        rules?.AllowedFolders.Count ?? 0, rules?.DeniedFolders.Count ?? 0, rules?.Applications.Count ?? 0,
                        Strings.T(p.Document?.Shell == true ? Msg.AccessYes : Msg.AccessNo)));
                }
            }

            lines.Add(Strings.T(Msg.AccessShowDefault, Strings.T(DefaultApplies(config, store, policy) ? Msg.AccessDefaultApplies : Msg.AccessCeilingOnly)));
            return lines;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or JsonException)
        {
            return [Strings.T(Msg.AccessShowNoStore, e.GetType().Name)];
        }
    }

    /// <summary>Whether a run whose repository has no policy gets the default: the workspace switch, or a strict machine (unsure counts as strict).</summary>
    private static bool DefaultApplies(AgentConfig config, LocalStore store, ExecPolicy policy) =>
        WorkspaceSettings.Get(config, store, AccessPolicySync.EnforceKey) == "true" || AccessLayers.IsStrict(null, policy, store)
        || store.Get(LocalStore.AccessTruncatedKey) == "true";

    private static int FolderCount(AppliedAccessPolicy judged, ExecPolicy policy, LocalStore store) =>
        judged.Keys.SelectMany(key => AccessFolders.For(key, policy.ProjectFolders, store, AgentConfig.Os)).Distinct(StringComparer.Ordinal).Count();
}
