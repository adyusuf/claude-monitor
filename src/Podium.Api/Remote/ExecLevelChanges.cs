using System.Text.Json;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Security;
using Podium.Api.Streaming;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Remote;

/// <summary>What the machine page reads: the sequence the next change must follow, the agent's last report and (owner and admins only) the pending request.</summary>
public sealed record ExecLevelView(Guid AgentId, string ExecLevel, long Sequence, ExecLevelReport? Report, DateTimeOffset? ReportAt, ExecLevelPendingView? Pending);

/// <summary>The request's details without its signature bytes.</summary>
public sealed record ExecLevelPendingView(string Level, long Sequence, DateTimeOffset NotAfter, bool IsSigned);

/// <summary>
/// The owner's change of a machine's remote-run level from the web (ADR-0021). Owner only, in an active membership; the level and deadline are checked,
/// the sequence is compare-and-set, a raise carries a signature of one of the owner's registered keys (the machine checks it again against its pinned
/// keys), and every change is re-authenticated like a grant. Nothing here changes <c>agents.exec_level</c>: only the agent's own report does.
/// </summary>
public static class ExecLevelChanges
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<IResult> PutAsync(Guid id, ExecLevelChange req, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock, Broker broker)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        var ct = http.RequestAborted;
        var userId = http.User.UserId();
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.Status == AgentStatuses.Active, ct);
        if (agent is null || agent.UserId != userId || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Viewer, ct) is null) return Http.NotFound();
        var now = clock.GetUtcNow();
        if (!ExecLevels.All.Contains(req.Level ?? "") || req.NotAfter <= now || req.NotAfter > now + ExecLevelSigning.MaxLifetime)
        {
            return Http.Invalid("level", ExecLevelErrors.Invalid);
        }

        if (req.Sequence != agent.ExecLevelSequence + 1) return RemoteChecks.Conflict(ExecLevelErrors.Changed);
        var signed = ExecLevelSigning.Raises(agent.ExecLevel, req.Level!);
        if (signed)
        {
            if (req.KeyId is not { Length: > 0 } || req.Signature is not { Length: > 0 }) return Http.Invalid("signature", ExecLevelErrors.SignatureRequired);
            if (!await VerifiesAsync(db, userId, req, id, ct)) return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: ApprovalErrors.Invalid);
        }

        if (await RemoteChecks.ApprovalGateAsync(http, db, config, clock, userId, agent, req.Code) is { } refused) return refused;
        var stored = new ExecLevelRequest(req.Level!, req.Sequence, req.NotAfter, signed ? req.KeyId : null, signed ? req.Signature : null);
        var json = JsonSerializer.Serialize(stored, Json);
        var won = await db.Agents.Where(a => a.Id == id && a.ExecLevelSequence == req.Sequence - 1)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ExecLevelSequence, req.Sequence).SetProperty(a => a.ExecLevelRequest, json), ct);
        if (won != 1) return RemoteChecks.Conflict(ExecLevelErrors.Changed);
        Audit.Add(db, http, clock, AuditActions.AgentExecLevelRequested, agent.WorkspaceId, userId, targetType: "agent", targetId: id,
            detail: new { level = req.Level, sequence = req.Sequence, signed });
        await db.SaveChangesAsync(ct);
        broker.Publish(Broker.Workspace(agent.WorkspaceId), new StreamMessage(AgentRoleEndpoints.MachineEvent, new { agentId = id }));
        return Results.Ok(View(await db.Agents.AsNoTracking().FirstAsync(a => a.Id == id, ct), true));
    }

    /// <summary>Members see the state; the pending request's details go to the machine's owner and to admins.</summary>
    public static async Task<IResult> GetAsync(Guid id, HttpContext http, PodiumDb db)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        var ct = http.RequestAborted;
        var userId = http.User.UserId();
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.Status == AgentStatuses.Active, ct);
        if (agent is null || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Viewer, ct) is null) return Http.NotFound();
        var details = agent.UserId == userId || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Admin, ct) is not null;
        return Results.Ok(View(agent, details));
    }

    private static ExecLevelView View(Agent agent, bool details)
    {
        var pending = details ? Read<ExecLevelRequest>(agent.ExecLevelRequest) : null;
        return new ExecLevelView(agent.Id, agent.ExecLevel, agent.ExecLevelSequence, Read<ExecLevelReport>(agent.ExecLevelReport), agent.ExecLevelReportAt,
            pending is null ? null : new ExecLevelPendingView(pending.Level, pending.Sequence, pending.NotAfter, pending.Signature is not null));
    }

    /// <summary>The stored document, or null when there is none; a stored value the API cannot read is treated as none.</summary>
    public static T? Read<T>(string? json) where T : class
    {
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The report to store: only a state of the closed set and a sequence that is not negative.</summary>
    public static string? Store(ExecLevelReport? report) =>
        report is { } r && ExecLevelStates.All.Contains(r.State ?? "") ? JsonSerializer.Serialize(new ExecLevelReport(Math.Max(0, r.Sequence), r.State!), Json) : null;

    /// <summary>True when the signature signs this change under one of the caller's registered keys that is not retired.</summary>
    private static async Task<bool> VerifiesAsync(PodiumDb db, Guid userId, ExecLevelChange req, Guid agentId, CancellationToken ct)
    {
        if (req.KeyId!.Length > PodiumDbModelAccess.KeyIdMax || req.Signature!.Length > PodiumDbModelAccess.SignatureMax) return false;
        var registered = await db.ApprovalKeys.AsNoTracking()
            .Where(k => k.UserId == userId && k.Fingerprint == req.KeyId && k.RetiredAt == null).Select(k => k.PublicKey).FirstOrDefaultAsync(ct);
        var key = ApprovalSigning.FromBase64(registered);
        var bytes = ApprovalSigning.FromBase64(req.Signature);
        return key is not null && bytes is not null && ApprovalSigning.Verify(key, ExecLevelSigning.Payload(agentId, req.Level, req.Sequence, req.NotAfter), bytes);
    }
}
