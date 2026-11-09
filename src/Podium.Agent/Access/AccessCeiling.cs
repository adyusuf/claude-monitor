using Podium.Agent.Auth;
using Podium.Agent.Config;
using Podium.Agent.Exec;
using Podium.Contracts;

namespace Podium.Agent.Access;

/// <summary>
/// The machine's own ceiling (ADR-0020 §4 rule 2): folders a run may use (empty: no limit of its own), folders it may never use and the executables it
/// may start, each with an optional SHA-256. A service keeps it in the admin-owned exec policy file, an interactive agent in agent.json; the machine's
/// admin edits it with <c>aipod config access</c>, never by hand. Every web policy is applied inside it.
/// </summary>
public sealed record AccessCeiling(IReadOnlyList<string> AllowedRoots, IReadOnlyList<string> DeniedRoots, IReadOnlyList<AccessApp> Executables)
{
    public static readonly AccessCeiling Empty = new([], [], []);

    public bool IsEmpty => AllowedRoots.Count == 0 && DeniedRoots.Count == 0 && Executables.Count == 0;

    /// <summary>The ceiling of an exec policy.</summary>
    public static AccessCeiling Of(ExecPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new AccessCeiling(policy.AllowedRoots, policy.DeniedRoots ?? [],
            [.. policy.AllowedExecutables.Select(e => new AccessApp(e, policy.ExecutableHashes?.GetValueOrDefault(e)))]);
    }

    /// <summary>The ceiling as the first access layer: its lists as listed (the guard resolves them) and shell only when the level allows it with no list.</summary>
    public static AccessLayer Layer(ExecPolicy policy)
    {
        var ceiling = Of(policy);
        return AccessPolicyMath.Ceiling(ceiling.AllowedRoots, ceiling.DeniedRoots, ceiling.Executables, policy.CeilingAllowsShell);
    }

    /// <summary>The ceiling as the machine reports it to the web (shown only to the machine's owner and the workspace's admins).</summary>
    public static AccessCeilingReport Report(ExecPolicy policy)
    {
        var ceiling = Of(policy);
        return new AccessCeilingReport(ceiling.AllowedRoots, ceiling.DeniedRoots, ceiling.Executables, policy.CeilingAllowsShell);
    }

    /// <summary>The ceiling written into an exec policy: the executables' hashes by path.</summary>
    public ExecPolicy ApplyTo(ExecPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var hashes = Executables.Where(a => a.Sha256 is not null).ToDictionary(a => a.Path, a => a.Sha256!, StringComparer.Ordinal);
        return policy with
        {
            AllowedRoots = AllowedRoots,
            AllowedExecutables = [.. Executables.Select(a => a.Path)],
            DeniedRoots = DeniedRoots.Count > 0 ? DeniedRoots : null,
            ExecutableHashes = hashes.Count > 0 ? hashes : null,
        };
    }
}

/// <summary>Where <c>aipod config access</c> reads and writes the ceiling: the service's admin-owned exec policy file, or agent.json.</summary>
internal static class AccessCeilingFile
{
    /// <summary>The service's file: in service mode, or when an admin runs the command on a machine whose service has its exec policy file.</summary>
    public static bool IsService(AgentConfig config) => config.ServiceMode || (ExecPolicyLoader.RunningAsRoot() && File.Exists(config.ExecConfigPath));

    /// <summary>The ceiling and the level it sits under, or null for a service without a valid exec policy file.</summary>
    public static (AccessCeiling Ceiling, ExecPolicy Policy)? Load(AgentConfig config)
    {
        if (IsService(config))
        {
            return ExecPolicyLoader.ReadExisting(config.ExecConfigPath) is { } file ? (AccessCeiling.Of(file), file) : null;
        }

        var saved = Identity.Peek(config);
        var ceiling = new AccessCeiling(saved?.AccessAllowedRoots ?? [], saved?.AccessDeniedRoots ?? [], saved?.AccessExecutables ?? []);
        var level = saved?.ExecLevel is { } l && ExecLevels.All.Contains(l) ? l : ExecLevels.Off;
        return (ceiling, ceiling.ApplyTo(new ExecPolicy(level, [], [])));
    }

    /// <summary>Saves the ceiling: the service's file through <see cref="ExecPolicyLoader.Save"/> (the admin's write), or agent.json.</summary>
    public static void Save(AgentConfig config, AccessCeiling ceiling)
    {
        if (IsService(config))
        {
            var file = ExecPolicyLoader.ReadExisting(config.ExecConfigPath) ?? throw new IOException("no valid exec policy file");
            ExecPolicyLoader.Save(config.ExecConfigPath, ceiling.ApplyTo(file));
            return;
        }

        var identity = Identity.Load(config);
        (identity with
        {
            AccessAllowedRoots = ceiling.AllowedRoots.Count > 0 ? [.. ceiling.AllowedRoots] : null,
            AccessDeniedRoots = ceiling.DeniedRoots.Count > 0 ? [.. ceiling.DeniedRoots] : null,
            AccessExecutables = ceiling.Executables.Count > 0 ? [.. ceiling.Executables] : null,
        }).Save(config);
    }
}
