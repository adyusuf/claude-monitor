using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Endpoints;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// The fixed actions a machine's owner asks of its agent from the web (ADR-0005, "Agent actions from the web"). The request is only
/// a kind from a closed set; the agent performs it with its own code. Fail-closed: a member of the workspace, the machine's owner,
/// and for restart and update a recent sign-in (a fresh two-step code on a strict target). Every state change is a compare-and-set
/// and is published after it is committed.
/// </summary>
public static class AgentActions
{
    public const string WorkspaceEvent = "action";
    public const int ListMax = 10;

    public static async Task<IResult> CreateAsync(Guid agentId, AgentActionRequest req, HttpContext http, MonitorDb db, ApiConfig config,
        TimeProvider clock, Broker broker)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        var userId = http.User.UserId();
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId && a.Status == AgentStatuses.Active, http.RequestAborted);
        if (agent is null || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        if (agent.UserId != userId) return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: AgentActionErrors.OwnerOnly);
        if (!AgentActionKinds.All.Contains(req.Kind ?? "")) return Http.Invalid("kind", AgentActionErrors.InvalidKind);
        var kind = req.Kind!;
        if (AgentActionKinds.NeedsReauth(kind)
            && await RemoteChecks.ApprovalGateAsync(http, db, config, clock, userId, agent, req.Code) is { } refused)
        {
            return refused;
        }

        var now = clock.GetUtcNow();
        await ExpireStaleAsync(db, agentId, now, http.RequestAborted);
        if (await OpenOfKindAsync(db, agentId, kind, http.RequestAborted) is { } open) return Results.Ok(View(open));

        var action = new AgentAction
        {
            WorkspaceId = agent.WorkspaceId,
            AgentId = agentId,
            RequestedBy = userId,
            Kind = kind,
            CreatedAt = now,
            ExpiresAt = now + AgentActionKinds.QueueTtl,
        };
        db.AgentActions.Add(action);
        Audit.Add(db, http, clock, AuditActions.AgentActionRequested, agent.WorkspaceId, userId, targetType: "agent", targetId: agentId,
            detail: new { kind });
        try
        {
            await db.SaveChangesAsync(http.RequestAborted);
        }
        catch (DbUpdateException)
        {
            // Another request of the same kind won the one-open-action index: that one is the answer.
            db.ChangeTracker.Clear();
            return await OpenOfKindAsync(db, agentId, kind, http.RequestAborted) is { } won ? Results.Ok(View(won)) : throw new InvalidOperationException("agent action not stored");
        }

        Changed(broker, action);
        broker.Publish(Broker.Agent(agentId), new StreamMessage(AgentStreamEvents.Action, new AgentActionMessage(action.Id, action.Kind, action.ExpiresAt)));
        return Results.Ok(View(action));
    }

    /// <summary>The owner's recent actions, newest first. Others get nothing: the result text can name this machine's folders.</summary>
    public static async Task<IResult> ListAsync(Guid agentId, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        var userId = http.User.UserId();
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId && a.Status == AgentStatuses.Active, http.RequestAborted);
        if (agent is null || agent.UserId != userId || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Viewer, http.RequestAborted) is null)
        {
            return Http.NotFound();
        }

        await ExpireStaleAsync(db, agentId, clock.GetUtcNow(), http.RequestAborted);
        var rows = await db.AgentActions.AsNoTracking().Where(a => a.AgentId == agentId)
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(ListMax).ToListAsync(http.RequestAborted);
        return Results.Ok(rows.Select(View).ToList());
    }

    /// <summary>The queued actions of an agent, for the replay when its stream opens. Delivered ones are not replayed: the agent already took them.</summary>
    public static async Task<List<AgentActionMessage>> WaitingForAsync(MonitorDb db, Guid agentId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        await ExpireStaleAsync(db, agentId, now, ct);
        return await db.AgentActions.AsNoTracking()
            .Where(a => a.AgentId == agentId && a.Status == AgentActionStatuses.Queued && a.ExpiresAt > now)
            .OrderBy(a => a.CreatedAt).Select(a => new AgentActionMessage(a.Id, a.Kind, a.ExpiresAt)).ToListAsync(ct);
    }

    /// <summary>
    /// The agent's report. "delivered" takes a queued action (the one and only start: a replayed message finds it taken and gets a 409) and gives it a
    /// fresh <see cref="AgentActionKinds.QueueTtl"/> to finish in, so a slow one is not expired just because it waited to be taken,
    /// "succeeded" and "failed" finish a delivered one. A report from another agent is a 404; a final status never changes.
    /// </summary>
    public static async Task<IResult> ReportAsync(Guid id, AgentActionReport req, Guid agentId, HttpContext http, MonitorDb db, TimeProvider clock,
        Broker broker)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        if (!AgentActionStatuses.FromAgent.Contains(req.Status ?? "")) return Http.Invalid("status", "invalid_status");
        var action = await db.AgentActions.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.AgentId == agentId, http.RequestAborted);
        if (action is null) return Http.NotFound();

        var now = clock.GetUtcNow();
        var result = req.Result is { Length: > AgentActionKinds.ResultMax } r ? r[..AgentActionKinds.ResultMax] : req.Result;
        var changed = req.Status == AgentActionStatuses.Delivered
            ? await db.AgentActions.Where(a => a.Id == id && a.Status == AgentActionStatuses.Queued && a.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AgentActionStatuses.Delivered).SetProperty(a => a.DeliveredAt, now).SetProperty(a => a.ExpiresAt, now + AgentActionKinds.QueueTtl), http.RequestAborted)
            : await db.AgentActions.Where(a => a.Id == id && a.Status == AgentActionStatuses.Delivered)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, req.Status!).SetProperty(a => a.Result, result).SetProperty(a => a.FinishedAt, now), http.RequestAborted);
        if (changed != 1) return Results.Conflict();
        action.Status = req.Status!;
        Changed(broker, action);
        return Results.NoContent();
    }

    public static AgentActionView View(AgentAction a) =>
        new(a.Id, a.AgentId, a.Kind, a.Status, a.Result, a.CreatedAt, a.ExpiresAt, a.FinishedAt);

    private static Task<AgentAction?> OpenOfKindAsync(MonitorDb db, Guid agentId, string kind, CancellationToken ct) =>
        db.AgentActions.AsNoTracking().FirstOrDefaultAsync(a => a.AgentId == agentId && a.Kind == kind
            && (a.Status == AgentActionStatuses.Queued || a.Status == AgentActionStatuses.Delivered), ct);

    /// <summary>An open action past its time is expired, so it no longer blocks a new one and is never delivered late.</summary>
    private static Task<int> ExpireStaleAsync(MonitorDb db, Guid agentId, DateTimeOffset now, CancellationToken ct) =>
        db.AgentActions.Where(a => a.AgentId == agentId && (a.Status == AgentActionStatuses.Queued || a.Status == AgentActionStatuses.Delivered)
                && a.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AgentActionStatuses.Expired).SetProperty(a => a.FinishedAt, now), ct);

    private static void Changed(Broker broker, AgentAction a) =>
        broker.Publish(Broker.Workspace(a.WorkspaceId), new StreamMessage(WorkspaceEvent, new { id = a.Id, agentId = a.AgentId, status = a.Status }));
}
