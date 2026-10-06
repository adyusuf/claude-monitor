using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Api.Text;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

public sealed record NameRequest(string? Name);
public sealed record WorkspaceResponse(Guid Id, string Name, string Role, SettingsResponse Settings);
public sealed record SettingsResponse(bool MaskSecrets, int RetentionDays, int EventMaxBytes, string AgentUpdate = UpdateModes.Off);
public sealed record SettingsRequest(bool? MaskSecrets, int? RetentionDays, int? EventMaxBytes, string? AgentUpdate = null);
public sealed record MemberResponse(Guid UserId, string DisplayName, string? Email, string Role, DateTimeOffset JoinedAt);
public sealed record RoleRequest(string? Role);

/// <summary>Workspaces and their members. Reads need any role; changes need admin; owners are protected.</summary>
public static class WorkspaceEndpoints
{
    public const int EventMaxBytesMin = 1024;
    public const int EventMaxBytesMax = 4 * 1024 * 1024;

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/workspaces").RequireAuthorization(Schemes.Session);
        g.MapPost("/", Create);
        g.MapGet("/{id:guid}", Get);
        g.MapPatch("/{id:guid}", Rename);
        g.MapPut("/{id:guid}/settings", PutSettings);
        g.MapGet("/{id:guid}/members", Members);
        g.MapPatch("/{id:guid}/members/{userId:guid}", ChangeRole);
        g.MapDelete("/{id:guid}/members/{userId:guid}", Remove);
    }

    private static async Task<IResult> Create(NameRequest req, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        if (!Http.IsName(req.Name)) return Http.Invalid("name", "invalid_name");
        var userId = http.User.UserId();
        var workspace = Accounts.NewWorkspace(db, clock.GetUtcNow(), userId, req.Name!);
        Audit.Add(db, http, clock, AuditActions.WorkspaceCreated, workspace.Id, userId);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Created($"/api/workspaces/{workspace.Id}", new { workspace.Id });
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, MonitorDb db)
    {
        var member = await Access.MemberAsync(db, http.User.UserId(), id, Roles.Viewer, http.RequestAborted);
        if (member is null) return Http.NotFound();
        var workspace = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == id, http.RequestAborted);
        var s = await db.WorkspaceSettings.AsNoTracking().FirstAsync(x => x.WorkspaceId == id, http.RequestAborted);
        return Results.Ok(new WorkspaceResponse(id, workspace.Name, member.Role, new(s.MaskSecrets, s.RetentionDays, s.EventMaxBytes, s.AgentUpdate)));
    }

    private static async Task<IResult> Rename(Guid id, NameRequest req, HttpContext http, MonitorDb db)
    {
        if (await Access.MemberAsync(db, http.User.UserId(), id, Roles.Admin, http.RequestAborted) is null) return Http.NotFound();
        if (!Http.IsName(req.Name)) return Http.Invalid("name", "invalid_name");
        var workspace = await db.Workspaces.FirstAsync(w => w.Id == id, http.RequestAborted);
        workspace.Name = req.Name!.Trim();
        workspace.NameSearch = SearchText.Normalize(workspace.Name);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> PutSettings(Guid id, SettingsRequest req, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        if (await Access.MemberAsync(db, userId, id, Roles.Admin, http.RequestAborted) is null) return Http.NotFound();
        if (req.RetentionDays is < 1 or > 3650) return Http.Invalid("retentionDays", "out_of_range");
        if (req.EventMaxBytes is < EventMaxBytesMin or > EventMaxBytesMax) return Http.Invalid("eventMaxBytes", "out_of_range");
        if (req.AgentUpdate is not null && !UpdateModes.IsValid(req.AgentUpdate)) return Http.Invalid("agentUpdate", "invalid_mode");

        var s = await db.WorkspaceSettings.FirstAsync(x => x.WorkspaceId == id, http.RequestAborted);
        var before = new SettingsResponse(s.MaskSecrets, s.RetentionDays, s.EventMaxBytes, s.AgentUpdate);
        s.MaskSecrets = req.MaskSecrets ?? s.MaskSecrets;
        s.RetentionDays = req.RetentionDays ?? s.RetentionDays;
        s.EventMaxBytes = req.EventMaxBytes ?? s.EventMaxBytes;
        s.AgentUpdate = req.AgentUpdate ?? s.AgentUpdate;
        s.UpdatedAt = clock.GetUtcNow();
        s.UpdatedBy = userId;
        Audit.Add(db, http, clock, AuditActions.SettingsChanged, id, userId,
            detail: new { before, after = new SettingsResponse(s.MaskSecrets, s.RetentionDays, s.EventMaxBytes, s.AgentUpdate) });
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> Members(Guid id, HttpContext http, MonitorDb db)
    {
        if (await Access.MemberAsync(db, http.User.UserId(), id, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        var members = await (from m in db.WorkspaceMembers.AsNoTracking()
                             join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                             where m.WorkspaceId == id && m.RemovedAt == null
                             orderby u.DisplayName
                             select new MemberResponse(u.Id, u.DisplayName, u.Email, m.Role, m.JoinedAt)).ToListAsync(http.RequestAborted);
        return Results.Ok(members);
    }

    private static async Task<IResult> ChangeRole(Guid id, Guid userId, RoleRequest req, HttpContext http, MonitorDb db,
        TimeProvider clock)
    {
        var actor = await Access.MemberAsync(db, http.User.UserId(), id, Roles.Admin, http.RequestAborted);
        if (actor is null) return Http.NotFound();
        if (req.Role is null || Roles.Rank(req.Role) == 0) return Http.Invalid("role", "invalid_role");
        var target = await db.WorkspaceMembers.FirstOrDefaultAsync(
            m => m.WorkspaceId == id && m.UserId == userId && m.RemovedAt == null, http.RequestAborted);
        if (target is null) return Http.NotFound();

        // Only an owner grants or takes away ownership; an admin cannot raise anyone above admin.
        var touchesOwner = req.Role == Roles.Owner || target.Role == Roles.Owner;
        if (touchesOwner && actor.Role != Roles.Owner) return Results.Forbid();
        if (target.Role == Roles.Owner && req.Role != Roles.Owner && await LastOwnerAsync(db, id, http.RequestAborted))
        {
            return Http.Invalid("role", "last_owner");
        }

        var before = target.Role;
        target.Role = req.Role;
        Audit.Add(db, http, clock, AuditActions.RoleChanged, id, actor.UserId, targetType: "user", targetId: userId,
            detail: new { before, after = req.Role });
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> Remove(Guid id, Guid userId, HttpContext http, MonitorDb db, TimeProvider clock, Broker broker)
    {
        var self = http.User.UserId();
        var actor = await Access.MemberAsync(db, self, id, self == userId ? Roles.Viewer : Roles.Admin, http.RequestAborted);
        if (actor is null) return Http.NotFound();
        var target = await db.WorkspaceMembers.FirstOrDefaultAsync(
            m => m.WorkspaceId == id && m.UserId == userId && m.RemovedAt == null, http.RequestAborted);
        if (target is null) return Http.NotFound();
        if (target.Role == Roles.Owner && self != userId && actor.Role != Roles.Owner) return Results.Forbid();
        if (target.Role == Roles.Owner && await LastOwnerAsync(db, id, http.RequestAborted)) return Http.Invalid("userId", "last_owner");

        var now = clock.GetUtcNow();
        // The removal, its agents' revocation and the end of their remote work commit together.
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        target.RemovedAt = now;
        Audit.Add(db, http, clock, AuditActions.MemberRemoved, id, self, targetType: "user", targetId: userId);
        var agents = await RevokeAgentsAsync(db, http, clock, id, userId, self, now);
        await db.SaveChangesAsync(http.RequestAborted);
        var notices = new List<Action>();
        foreach (var agentId in agents) await RemoteCleanup.ForAgentAsync(db, broker, agentId, self, now, http.RequestAborted, notices);
        await RemoteCleanup.ForMemberAsync(db, broker, id, userId, self, now, http.RequestAborted, notices);
        await tx.CommitAsync(http.RequestAborted);
        notices.ForEach(n => n());
        foreach (var agentId in agents) broker.Publish(Broker.Agent(agentId), new StreamMessage(AgentStreamEvents.Revoked, new { }));

        return Results.NoContent();
    }

    /// <summary>A removed member's agents in this workspace stop at once: they are revoked with their tokens.</summary>
    private static async Task<List<Guid>> RevokeAgentsAsync(
        MonitorDb db, HttpContext http, TimeProvider clock, Guid workspaceId, Guid userId, Guid actor, DateTimeOffset now)
    {
        var agents = await db.Agents
            .Where(a => a.WorkspaceId == workspaceId && a.UserId == userId && a.Status == AgentStatuses.Active)
            .ToListAsync(http.RequestAborted);
        foreach (var agent in agents)
        {
            agent.Status = AgentStatuses.Revoked;
            agent.RevokedAt = now;
            agent.RevokedBy = actor;
            await AgentTokens.RevokeAllAsync(db, agent.Id, now, http.RequestAborted);
            Audit.Add(db, http, clock, AuditActions.AgentRevoked, workspaceId, actor, targetType: "agent", targetId: agent.Id,
                detail: new { reason = "member_removed" });
        }

        return agents.Select(a => a.Id).ToList();
    }

    private static async Task<bool> LastOwnerAsync(MonitorDb db, Guid workspaceId, CancellationToken ct) =>
        await db.WorkspaceMembers.CountAsync(m => m.WorkspaceId == workspaceId && m.RemovedAt == null && m.Role == Roles.Owner, ct) <= 1;
}
