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

    /// <summary>Handles the request a settings pass delivered; never throws. Returns true when it was new (a profile is worth sending soon).</summary>
    public static bool Handle(AgentConfig config, LocalStore store, TimeProvider clock, AgentLog log, ExecLevelRequest? request)
    {
        if (request is null) return false;
        try
        {
            lock (Gate)
            {
                if (long.TryParse(store.Get(SequenceKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var last) && request.Sequence <= last) return false;
                var state = Decide(config, request, clock.GetUtcNow());
                store.SetMany([(SequenceKey, request.Sequence.ToString(CultureInfo.InvariantCulture)), (StateKey, state), (LevelKey, Safe(request.Level))]);
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

    /// <summary>What the last change did, for the profile; null when none was handled.</summary>
    public static ExecLevelReport? Reported(LocalStore store) =>
        long.TryParse(store.Get(SequenceKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence) && store.Get(StateKey) is { Length: > 0 } state
            ? new ExecLevelReport(sequence, state) : null;

    /// <summary>One line for `aipod status`, or null when no change from the web was handled.</summary>
    public static string? Describe(LocalStore store) =>
        Reported(store) is { } r ? Strings.T(Msg.StatusExecChange, r.Sequence, store.Get(LevelKey) ?? "", r.State) : null;

    private static string Decide(AgentConfig config, ExecLevelRequest request, DateTimeOffset now)
    {
        var level = request.Level;
        if (ExecLevelSigning.Rank(level) < 0) return ExecLevelStates.SignatureRefused;
        if (now > request.NotAfter || request.NotAfter - now > ExecLevelSigning.MaxLifetime) return ExecLevelStates.Expired;
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
