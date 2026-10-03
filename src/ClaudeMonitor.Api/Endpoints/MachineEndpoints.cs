using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

public sealed record AgentRow(Guid Id, Guid MachineId, string Hostname, string Os, string Arch, string Version, string Status,
    Guid UserId, string UserName, DateTimeOffset EnrolledAt, DateTimeOffset? LastHeartbeatAt, DateTimeOffset? RevokedAt);
public sealed record MoveRequest(Guid? WorkspaceId);
public sealed record AuditRow(long Id, string Action, Guid? ActorUserId, Guid? ActorAgentId, string? TargetType, string? TargetId,
    DateTimeOffset At, System.Text.Json.JsonDocument? Detail);

/// <summary>Connected machines and their agents; the workspace's audit log.</summary>
public static class MachineEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/workspaces/{id:guid}/agents", List);
        g.MapPost("/agents/{id:guid}/revoke", Revoke);
        g.MapPatch("/agents/{id:guid}", Move);
        g.MapGet("/workspaces/{id:guid}/audit", AuditLog);
    }

    private static async Task<IResult> List(Guid id, HttpContext http, MonitorDb db)
    {
        if (await Access.MemberAsync(db, http.User.UserId(), id, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        var rows = await (from a in db.Agents.AsNoTracking()
                          join m in db.Machines.AsNoTracking() on a.MachineId equals m.Id
                          join u in db.Users.AsNoTracking() on a.UserId equals u.Id
                          where a.WorkspaceId == id
                          orderby a.Status, m.Hostname, a.EnrolledAt descending
                          select new AgentRow(a.Id, m.Id, m.Hostname, m.Os, m.Arch, a.Version, a.Status, u.Id, u.DisplayName,
                              a.EnrolledAt, a.LastHeartbeatAt, a.RevokedAt)).Take(500).ToListAsync(http.RequestAborted);
        return Results.Ok(rows);
    }

    /// <summary>The agent's own user, or an admin of its workspace, may disconnect it.</summary>
    private static async Task<IResult> Revoke(Guid id, HttpContext http, MonitorDb db, TimeProvider clock, Broker broker)
    {
        var userId = http.User.UserId();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id, http.RequestAborted);
        if (agent is null) return Http.NotFound();
        var allowed = agent.UserId == userId
            || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Admin, http.RequestAborted) is not null;
        if (!allowed) return Http.NotFound();
        if (agent.Status == AgentStatuses.Revoked) return Results.NoContent();

        var now = clock.GetUtcNow();
        agent.Status = AgentStatuses.Revoked;
        agent.RevokedAt = now;
        agent.RevokedBy = userId;
        await AgentTokens.RevokeAllAsync(db, agent.Id, now, http.RequestAborted);
        Audit.Add(db, http, clock, AuditActions.AgentRevoked, agent.WorkspaceId, userId, targetType: "agent", targetId: id);
        await db.SaveChangesAsync(http.RequestAborted);
        broker.Publish(Broker.Agent(id), new StreamMessage(AgentStreamEvents.Revoked, new { }));
        return Results.NoContent();
    }

    /// <summary>Only the agent's own user moves it, and only into a workspace where they may contribute.
    /// Sessions already captured stay where they were recorded.</summary>
    private static async Task<IResult> Move(Guid id, MoveRequest req, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId && a.Status == AgentStatuses.Active,
            http.RequestAborted);
        if (agent is null) return Http.NotFound();
        if (req.WorkspaceId is not { } target || await Access.MemberAsync(db, userId, target, Roles.Member, http.RequestAborted) is null)
        {
            return Http.Invalid("workspaceId", "not_a_member");
        }

        var from = agent.WorkspaceId;
        var machine = await db.Machines.FirstAsync(m => m.Id == agent.MachineId, http.RequestAborted);
        var now = clock.GetUtcNow();
        var targetMachine = await db.Machines.FirstOrDefaultAsync(
            m => m.WorkspaceId == target && m.MachineKeyHash == machine.MachineKeyHash, http.RequestAborted);
        if (targetMachine is null)
        {
            targetMachine = new Machine
            {
                WorkspaceId = target,
                MachineKeyHash = machine.MachineKeyHash,
                Hostname = machine.Hostname,
                Os = machine.Os,
                OsVersion = machine.OsVersion,
                Arch = machine.Arch,
                FirstSeenAt = now,
                LastSeenAt = now,
            };
            db.Machines.Add(targetMachine);
        }

        agent.MachineId = targetMachine.Id;
        agent.WorkspaceId = target;
        Audit.Add(db, http, clock, AuditActions.AgentMoved, target, userId, targetType: "agent", targetId: id, detail: new { from, to = target });
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> AuditLog(Guid id, long? before, int? limit, HttpContext http, MonitorDb db, Config.ApiConfig config)
    {
        if (await Access.MemberAsync(db, http.User.UserId(), id, Roles.Admin, http.RequestAborted) is null) return Http.NotFound();
        var take = Http.Limit(limit, config);
        var query = db.AuditEvents.AsNoTracking().Where(a => a.WorkspaceId == id);
        if (before is { } b) query = query.Where(a => a.Id < b);
        var rows = await query.OrderByDescending(a => a.Id).Take(take + 1)
            .Select(a => new AuditRow(a.Id, a.Action, a.ActorUserId, a.ActorAgentId, a.TargetType, a.TargetId, a.At, a.Detail))
            .ToListAsync(http.RequestAborted);
        var next = rows.Count > take ? rows[take - 1].Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return Results.Ok(new Page<AuditRow>(rows.Take(take).ToList(), next));
    }
}
