using System.Globalization;
using Podium.Agent.Approvals;
using Podium.Agent.Auth;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Exec;

/// <summary>
/// What this machine does with a remote-run level the owner asked for on the web (ADR-0021). Raising it needs the signature of one of the owner keys
/// pinned here; lowering does not. A service's level lives in an admin-owned file it cannot write, so it only ever reports <c>needs_admin</c> (or lowers
/// the file when it happens to be writable). A sequence is handled once, whatever the outcome.
/// </summary>
public static class ExecLevelChanges
{
    public const string SequenceKey = "exec_level.sequence";
    public const string StateKey = "exec_level.state";
    public const string LevelKey = "exec_level.level";

    private static readonly Lock Gate = new();

    /// <summary>An unsigned change may skip this many sequences (an agent that was offline missed some); a bigger jump is taken as stale, so a
    /// compromised server cannot push the count out of reach with one unsigned lowering.</summary>
    public const long UnsignedJumpMax = 1000;

    /// <summary>A clock this far behind the browser's still accepts a change signed with the web's lifetime.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Handles the request a settings pass delivered; never throws. The handled sequence is kept per agent id, so a new login (a new agent) starts the
    /// count over and an old agent's signed change can never apply to the new one. The request the API keeps delivering again is silent; an older or
    /// implausible one is reported as stale once.
    /// </summary>
    public static bool Handle(AgentConfig config, LocalStore store, TimeProvider clock, AgentLog log, ExecLevelRequest? request)
    {
        if (request is null || Identity.Peek(config)?.AgentId is not { } agent) return false;
        try
        {
            lock (Gate)
            {
                var last = LastSequence(store, agent);
                if (request.Sequence == last) return false;
                var stale = request.Sequence < last || (request.Signature is null && request.Sequence - last > UnsignedJumpMax);
                if (stale && store.Get(StateKey) == ExecLevelStates.Stale && store.Get(StaleKey) == Key(agent, request.Sequence)) return false;
                var state = stale ? ExecLevelStates.Stale : Decide(config, request, clock.GetUtcNow());
                List<(string, string)> entries = [(StateKey, state), (LevelKey, Safe(request.Level)), (StaleKey, stale ? Key(agent, request.Sequence) : "")];
                if (!stale) entries.Add((SequenceKey, Key(agent, request.Sequence)));
                store.SetMany(entries);
                log.Write($"exec level change from the web: sequence {request.Sequence}, level {Safe(request.Level)}, {state}");
                return true;
            }
        }
        catch (Exception e)
        {
            // the settings loop goes on; the change is tried again at the next pass because its sequence was not recorded
            log.Write($"exec level change failed ({e.GetType().Name})");
            return false;
        }
    }

    private const string StaleKey = "exec_level.stale";

    private static string Key(Guid agent, long sequence) => $"{agent:D}:{sequence.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The last sequence handled for this agent; 0 for another agent (an earlier login) or none.</summary>
    private static long LastSequence(LocalStore store, Guid agent)
    {
        var stored = store.Get(SequenceKey);
        var cut = stored?.LastIndexOf(':') ?? -1;
        return cut > 0 && Guid.TryParse(stored![..cut], out var owner) && owner == agent
            && long.TryParse(stored[(cut + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence) ? sequence : 0;
    }

    /// <summary>What the last change did, for the profile; null when none was handled for the agent logged in now.</summary>
    public static ExecLevelReport? Reported(LocalStore store, Guid? agent = null)
    {
        if (store.Get(StateKey) is not { Length: > 0 } state) return null;
        var stored = state == ExecLevelStates.Stale ? store.Get(StaleKey) : store.Get(SequenceKey);
        var cut = stored?.LastIndexOf(':') ?? -1;
        if (cut <= 0 || !Guid.TryParse(stored![..cut], out var owner) || (agent is { } current && owner != current)) return null;
        return long.TryParse(stored[(cut + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence) ? new ExecLevelReport(sequence, state) : null;
    }

    /// <summary>One line for `aipod status`, or null when no change from the web was handled.</summary>
    public static string? Describe(LocalStore store, Guid? agent = null) =>
        Reported(store, agent) is { } r ? Strings.T(Msg.StatusExecChange, r.Sequence, store.Get(LevelKey) ?? "", r.State) : null;

    private static string Decide(AgentConfig config, ExecLevelRequest request, DateTimeOffset now)
    {
        var level = request.Level;
        if (ExecLevelSigning.Rank(level) < 0) return ExecLevelStates.Invalid;
        if (now > request.NotAfter || request.NotAfter - now > ExecLevelSigning.MaxLifetime + ClockSkew) return ExecLevelStates.Expired;
        if (ExecLevelSigning.Raises(Current(config), level) && Refusal(config, request) is { } refused) return refused;
        if (config.ServiceMode) return ServiceChange(config, level);
        (Identity.Load(config) with { ExecLevel = level }).Save(config);
        return ExecLevelStates.Applied;
    }

    /// <summary>The level in force as the machine keeps it: the admin file of a service (read as written), agent.json otherwise.</summary>
    private static string? Current(AgentConfig config) =>
        config.ServiceMode ? ExecPolicyLoader.ReadExisting(config.ExecConfigPath)?.Level : Identity.Peek(config)?.ExecLevel;

    /// <summary>The state to report for a raise that cannot be accepted, or null when a pinned key signed exactly this change.</summary>
    private static string? Refusal(AgentConfig config, ExecLevelRequest request)
    {
        var keys = ApproversStore.Load(config).Keys;
        if (keys.Count == 0) return ExecLevelStates.NoPinnedKey;
        var key = keys.FirstOrDefault(k => string.Equals(k.Id, request.KeyId, StringComparison.OrdinalIgnoreCase));
        var agent = Identity.Peek(config)?.AgentId;
        if (key is null || agent is not { } id || request.Signature is null) return ExecLevelStates.SignatureRefused;
        var spki = ApprovalSigning.FromBase64(key.PublicKey);
        var signature = ApprovalSigning.FromBase64(request.Signature);
        var payload = ExecLevelSigning.Payload(id, request.Level, request.Sequence, request.NotAfter);
        return spki is not null && signature is not null && ApprovalSigning.Verify(spki, payload, signature) ? null : ExecLevelStates.SignatureRefused;
    }

    /// <summary>A service never raises its own level; it lowers the admin file only when that file is writable by it, otherwise the admin has to.</summary>
    private static string ServiceChange(AgentConfig config, string level)
    {
        var existing = ExecPolicyLoader.ReadExisting(config.ExecConfigPath);
        if (existing is null) return ExecLevelStates.NeedsAdmin;
        if (existing.Level == level) return ExecLevelStates.Applied;
        if (ExecLevelSigning.Rank(level) > ExecLevelSigning.Rank(existing.Level)) return ExecLevelStates.NeedsAdmin;
        try
        {
            ExecPolicyLoader.Save(config.ExecConfigPath, existing with { Level = level });
            return ExecLevelStates.Applied;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ExecLevelStates.NeedsAdmin;
        }
    }

    private static string Safe(string? level) => ExecLevels.All.Contains(level ?? "") ? level! : "(unknown)";
}
