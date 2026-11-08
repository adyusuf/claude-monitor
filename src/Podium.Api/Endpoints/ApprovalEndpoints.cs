using Podium.Api.Approvals;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Streaming;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>A permission request on one of the caller's sessions, with where it comes from (ADR-0019 §11-13).</summary>
public sealed record ApprovalRequestRow(Guid Id, Guid SessionId, string? SessionTitle, string? ProjectName, Guid AgentId, string Hostname,
    string ToolName, System.Text.Json.JsonDocument ToolInput, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    string? Decision, DateTimeOffset? AnsweredAt, string? AnsweredByName, Guid? AnsweredByRule, bool CanRemember, bool Strict);

/// <summary>One of the caller's approval rules.</summary>
public sealed record ApprovalRuleRow(Guid Id, Guid AgentId, string Hostname, string ToolName, string? Command, string Duration,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, int UseCount, DateTimeOffset? LastUsedAt, string? ProjectName = null);

/// <summary>
/// The approvals page and band (ADR-0019): the permission requests on the caller's own sessions, the caller's own approval rules, and revoking
/// one. Nobody reads another user's rules; requests are only those of sessions the caller owns (their agent runs them).
/// </summary>
public static class ApprovalEndpoints
{
    public const int Take = 50;

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/workspaces/{id:guid}/permission-requests", Requests);
        g.MapGet("/workspaces/{id:guid}/approval-rules", Rules);
        g.MapDelete("/approval-rules/{id:guid}", Revoke);
    }

    /// <summary>Open requests (oldest first) and, with <c>status=answered</c>, the latest answered ones; only on the caller's sessions.</summary>
    private static async Task<IResult> Requests(Guid id, string? status, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var ct = http.RequestAborted;
        if (await Access.MemberAsync(db, userId, id, Roles.Viewer, ct) is null) return Http.NotFound();
        var now = clock.GetUtcNow();
        var answered = status == PermissionStatuses.Answered;
        var query = from p in db.PermissionRequests.AsNoTracking()
                    join a in db.Agents.AsNoTracking() on p.AgentId equals a.Id
                    join m in db.Machines.AsNoTracking() on a.MachineId equals m.Id
                    join s in db.HarnessSessions.AsNoTracking() on p.SessionId equals s.Id
                    where p.WorkspaceId == id && a.UserId == userId
                    select new { p, a, m.Hostname, s.Title, s.RepoId };
        query = answered
            ? query.Where(x => x.p.Status == PermissionStatuses.Answered).OrderByDescending(x => x.p.AnsweredAt)
            : query.Where(x => x.p.Status == PermissionStatuses.Open && x.p.ExpiresAt > now).OrderBy(x => x.p.CreatedAt);
        var rows = await query.Take(Take).ToListAsync(ct);
        var repoIds = rows.Where(r => r.RepoId != null).Select(r => r.RepoId!.Value).Distinct().ToList();
        var repos = await db.Repos.AsNoTracking().Where(r => repoIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.DisplayName, ct);
        var userIds = rows.Where(r => r.p.AnsweredBy != null).Select(r => r.p.AnsweredBy!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return Results.Ok(rows.Select(r =>
        {
            var strict = ApprovalRules.Strict(r.a);
            var canRemember = !strict && ApprovalRules.ScopeOf(r.p.ToolName, r.p.ToolInput.RootElement) is not null;
            return new ApprovalRequestRow(r.p.Id, r.p.SessionId, r.Title, r.RepoId is { } rid ? repos.GetValueOrDefault(rid) : null, r.a.Id, r.Hostname,
                r.p.ToolName, r.p.ToolInput, r.p.Status, r.p.CreatedAt, r.p.ExpiresAt, r.p.Decision, r.p.AnsweredAt,
                r.p.AnsweredBy is { } by ? names.GetValueOrDefault(by) : null, r.p.AnsweredByRule, canRemember, strict);
        }).ToList());
    }

    /// <summary>The caller's live rules in this workspace, the newest first.</summary>
    private static async Task<IResult> Rules(Guid id, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var ct = http.RequestAborted;
        if (await Access.MemberAsync(db, userId, id, Roles.Viewer, ct) is null) return Http.NotFound();
        var now = clock.GetUtcNow();
        var rows = await (from r in db.ApprovalRules.AsNoTracking()
                          join a in db.Agents.AsNoTracking() on r.AgentId equals a.Id
                          join m in db.Machines.AsNoTracking() on a.MachineId equals m.Id
                          join p in db.Repos.AsNoTracking() on r.RepoId equals (Guid?)p.Id into projects
                          from p in projects.DefaultIfEmpty()
                          where r.WorkspaceId == id && r.UserId == userId && r.RevokedAt == null && (r.ExpiresAt == null || r.ExpiresAt > now)
                          orderby r.CreatedAt descending
                          select new ApprovalRuleRow(r.Id, r.AgentId, m.Hostname, r.ToolName, r.Command, r.Duration, r.CreatedAt, r.ExpiresAt,
                              r.UseCount, r.LastUsedAt, p == null ? null : p.DisplayName))
            .Take(ApprovalRuleLimits.LiveMax).ToListAsync(ct);
        return Results.Ok(rows);
    }

    /// <summary>Revokes one of the caller's rules (compare-and-set: a rule revoked twice answers 404 the second time).</summary>
    private static async Task<IResult> Revoke(Guid id, HttpContext http, PodiumDb db, TimeProvider clock, Broker broker)
    {
        var userId = http.User.UserId();
        var ct = http.RequestAborted;
        var rule = await db.ApprovalRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);
        if (rule is null || await Access.MemberAsync(db, userId, rule.WorkspaceId, Roles.Viewer, ct) is null) return Http.NotFound();
        var now = clock.GetUtcNow();
        var changed = await db.ApprovalRules.Where(r => r.Id == id && r.UserId == userId && r.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, now), ct);
        if (changed != 1) return Http.NotFound();
        Audit.Add(db, http, clock, AuditActions.ApprovalRuleRevoked, rule.WorkspaceId, userId, targetType: "approval_rule", targetId: id,
            detail: new { rule.ToolName, rule.Duration });
        await db.SaveChangesAsync(ct);
        broker.Publish(Broker.Workspace(rule.WorkspaceId), new StreamMessage("approval_rule", new { id }));
        return Results.NoContent();
    }
}
