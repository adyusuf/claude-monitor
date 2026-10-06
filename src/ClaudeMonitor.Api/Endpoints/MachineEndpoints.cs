using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Api.Update;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

public sealed record AgentRow(Guid Id, Guid MachineId, string Hostname, string Os, string Arch, string Version, string Status,
    Guid UserId, string UserName, DateTimeOffset EnrolledAt, DateTimeOffset? LastHeartbeatAt, DateTimeOffset? RevokedAt, string? LatestVersion = null,
    bool UpdateAvailable = false);
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

    private static async Task<IResult> List(Guid id, HttpContext http, MonitorDb db, UpdateCatalog catalog)
    {
        if (await Access.MemberAsync(db, http.User.UserId(), id, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        var rows = await (from a in db.Agents.AsNoTracking()
                          join m in db.Machines.AsNoTracking() on a.MachineId equals m.Id
                          join u in db.Users.AsNoTracking() on a.UserId equals u.Id
                          where a.WorkspaceId == id
                          orderby a.Status, m.Hostname, a.EnrolledAt descending
                          select new AgentRow(a.Id, m.Id, m.Hostname, m.Os, m.Arch, a.Version, a.Status, u.Id, u.DisplayName,
                              a.EnrolledAt, a.LastHeartbeatAt, a.RevokedAt)).Take(500).ToListAsync(http.RequestAborted);
        return Results.Ok(rows.Select(r => WithUpdate(r, catalog)));
    }

    /// <summary>Whether the newest build this server hands out is newer than the one the agent runs (decided here, never in the browser).</summary>
    private static AgentRow WithUpdate(AgentRow row, UpdateCatalog catalog)
    {
        if (row.Status != AgentStatuses.Active || catalog.Latest(row.Os, row.Arch) is not { } offer) return row;
        var newer = Version.TryParse(offer.Version, out var latest) && Version.TryParse(row.Version, out var current) && latest > current;
        return row with { LatestVersion = offer.Version, UpdateAvailable = newer };
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
        // The revoke and the end of its remote work (grants, jobs, alerts, open runs) commit together.
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        agent.Status = AgentStatuses.Revoked;
        agent.RevokedAt = now;
        agent.RevokedBy = userId;
        await AgentTokens.RevokeAllAsync(db, agent.Id, now, http.RequestAborted);
        Audit.Add(db, http, clock, AuditActions.AgentRevoked, agent.WorkspaceId, userId, targetType: "agent", targetId: id);
        await db.SaveChangesAsync(http.RequestAborted);
        var notices = new List<Action>();
        await RemoteCleanup.ForAgentAsync(db, broker, id, userId, now, http.RequestAborted, notices);
        await tx.CommitAsync(http.RequestAborted);
        notices.ForEach(n => n());
        broker.Publish(Broker.Agent(id), new StreamMessage(AgentStreamEvents.Revoked, new { }));
        return Results.NoContent();
    }

    /// <summary>Only the agent's own user moves it, and only into a workspace where they may contribute.
    /// Sessions already captured stay where they were recorded.</summary>
    private static async Task<IResult> Move(Guid id, MoveRequest req, HttpContext http, MonitorDb db, TimeProvider clock, Broker broker)
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
        // The move and the end of the agent's remote work in its old workspace commit together.
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        await db.SaveChangesAsync(http.RequestAborted);
        var notices = new List<Action>();
        await RemoteCleanup.ForAgentAsync(db, broker, id, userId, now, http.RequestAborted, notices);
        await tx.CommitAsync(http.RequestAborted);
        notices.ForEach(n => n());
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
