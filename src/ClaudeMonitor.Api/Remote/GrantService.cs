using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Endpoints;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ClaudeMonitor.Api.Remote;

/// <summary>The owner's approval of a grant: Code is a TOTP or recovery code, needed when the sign-in is not recent.</summary>
public sealed record GrantApproval(string? Code);

/// <summary>An owner gives a grant directly (active at once). The grantee defaults to the owner.</summary>
public sealed record WebGrantRequest(IReadOnlyList<string> Template, string Cwd, int MaxTimeoutSeconds, int Days, string? Reason,
    Guid? GranteeUserId = null, string? Code = null);

/// <summary>
/// Grants as they are asked for and decided (ADR-0004). Only the target agent's owner approves, denies or creates one,
/// the owner and the grantee revoke; approving and creating need a recent sign-in or a code. Every transition is a
/// compare-and-set on the status, the audit row is saved with it, and the stream hears of it after the commit.
/// </summary>
public sealed class GrantService(MonitorDb db, ApiConfig config, TimeProvider clock, Broker broker)
{
    public const string WorkspaceEvent = "grant";
    public const string Conflicting = "grant_conflict";
    private const string UniqueViolation = "23505";

    private sealed record Target(Agent Agent, string Os);

    /// <summary>A requester's agent asks for a grant: only a request until the target's owner approves it.</summary>
    public async Task<IResult> RequestAsync(GrantRequest req, Guid agentId, Guid userId, Guid workspaceId, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(req);
        var ct = http.RequestAborted;
        var settings = await db.WorkspaceSettings.AsNoTracking().FirstAsync(s => s.WorkspaceId == workspaceId, ct);
        if (!settings.RemoteRunsEnabled) return RemoteChecks.Conflict(RemoteErrors.Disabled);
        if (await Access.MemberAsync(db, userId, workspaceId, Roles.Member, ct) is null) return Http.NotFound();
        var target = await TargetAsync(req.TargetAgentId, workspaceId, ct);
        if (target is null) return Http.NotFound();
        if (Shape(req.Days, req.Reason) is { } shape) return shape;
        if (RemoteChecks.TemplateProblem(req.Template, req.Cwd, req.MaxTimeoutSeconds, target.Os, true) is { } bad) return bad;

        var grant = New(workspaceId, target.Agent, userId, req.Template, req.Cwd, req.MaxTimeoutSeconds, req.Days, req.Reason);
        grant.GranteeAgentId = agentId;
        grant.RequestedByAgentId = agentId;
        var existing = await LiveAsync(grant, ct);
        if (existing is null
            && await db.MachineGrants.CountAsync(g => g.TargetAgentId == grant.TargetAgentId && g.Status == GrantStatuses.Requested, ct)
               >= config.RunPendingMaxPerTarget)
        {
            return RemoteChecks.Conflict(RemoteErrors.TargetBusy);
        }

        var stored = existing ?? await InsertAsync(grant, http, AuditActions.GrantRequested, agentId, ct);
        return stored is null ? RemoteChecks.Conflict(Conflicting) : Results.Ok(GrantQueries.View(stored));
    }

    /// <summary>The target agent's owner gives a grant directly, after re-authenticating.</summary>
    public async Task<IResult> CreateAsync(Guid agentId, WebGrantRequest req, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(req);
        var ct = http.RequestAborted;
        var userId = http.User.UserId();
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId && a.UserId == userId, ct);
        if (agent is null || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Member, ct) is null) return Http.NotFound();
        var target = await TargetAsync(agentId, agent.WorkspaceId, ct);
        if (target is null) return Http.NotFound();
        var settings = await db.WorkspaceSettings.AsNoTracking().FirstAsync(s => s.WorkspaceId == agent.WorkspaceId, ct);
        if (!settings.RemoteRunsEnabled) return RemoteChecks.Conflict(RemoteErrors.Disabled);
        if (Shape(req.Days, req.Reason) is { } shape) return shape;
        var granteeId = req.GranteeUserId ?? userId;
        if (await Access.MemberAsync(db, granteeId, agent.WorkspaceId, Roles.Member, ct) is null) return Http.Invalid("granteeUserId", "not_member");
        if (RemoteChecks.TemplateProblem(req.Template, req.Cwd, req.MaxTimeoutSeconds, target.Os, false) is { } bad) return bad;
        if (!await RemoteChecks.ReauthAsync(http, db, config, clock, userId, req.Code)) return RemoteChecks.ReauthRefused();

        var now = clock.GetUtcNow();
        var grant = New(agent.WorkspaceId, agent, granteeId, req.Template, req.Cwd, req.MaxTimeoutSeconds, req.Days, req.Reason);
        grant.Status = GrantStatuses.Active;
        grant.DecidedAt = now;
        grant.DecidedBy = userId;
        var stored = await LiveAsync(grant, ct) ?? await InsertAsync(grant, http, AuditActions.GrantApproved, null, ct);
        if (stored is null) return RemoteChecks.Conflict(Conflicting);
        var views = await GrantQueries.ForWebAsync(db, agentId, userId, true, stored.Id, null, 1, now, ct);
        return views.Count == 1 ? Results.Ok(views[0]) : Http.NotFound();
    }

    public async Task<IResult> ApproveAsync(Guid id, GrantApproval? req, HttpContext http)
    {
        var ct = http.RequestAborted;
        var userId = http.User.UserId();
        var now = clock.GetUtcNow();
        var grant = await OwnedAsync(id, userId, ct);
        if (grant is null) return Http.NotFound();
        if (grant.Status != GrantStatuses.Requested || grant.ExpiresAt <= now || grant.Template is not { } template || grant.Cwd is not { } cwd)
        {
            return RemoteChecks.Conflict(RemoteErrors.NotPending);
        }

        var target = await TargetAsync(grant.TargetAgentId, grant.WorkspaceId, ct);
        if (target is null || await Access.MemberAsync(db, grant.GranteeUserId, grant.WorkspaceId, Roles.Member, ct) is null)
        {
            return RemoteChecks.Conflict(RemoteErrors.TargetCannotRun);
        }

        if (RemoteChecks.TemplateProblem(template, cwd, grant.MaxTimeoutSeconds, target.Os, false) is { } bad) return bad;
        if (!await RemoteChecks.ReauthAsync(http, db, config, clock, userId, req?.Code)) return RemoteChecks.ReauthRefused();

        var changed = await db.MachineGrants
            .Where(g => g.Id == id && g.Status == GrantStatuses.Requested && g.ExpiresAt > now && g.OwnerUserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, GrantStatuses.Active).SetProperty(g => g.DecidedAt, now)
                .SetProperty(g => g.DecidedBy, userId), ct);
        if (changed != 1) return RemoteChecks.Conflict(RemoteErrors.NotPending);
        Audit.Add(db, http, clock, AuditActions.GrantApproved, grant.WorkspaceId, userId, targetType: "grant", targetId: id,
            detail: new { hash = grant.TemplateHash, target = grant.TargetAgentId });
        await db.SaveChangesAsync(ct);
        grant.Status = GrantStatuses.Active;
        Changed(grant);
        return Results.NoContent();
    }

    public async Task<IResult> DenyAsync(Guid id, HttpContext http)
    {
        var ct = http.RequestAborted;
        var userId = http.User.UserId();
        var now = clock.GetUtcNow();
        var grant = await OwnedAsync(id, userId, ct);
        if (grant is null) return Http.NotFound();
        var changed = await db.MachineGrants.Where(g => g.Id == id && g.Status == GrantStatuses.Requested && g.OwnerUserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, GrantStatuses.Denied).SetProperty(g => g.DecidedAt, now)
                .SetProperty(g => g.DecidedBy, userId), ct);
        if (changed != 1) return RemoteChecks.Conflict(RemoteErrors.NotPending);
        Audit.Add(db, http, clock, AuditActions.GrantDenied, grant.WorkspaceId, userId, targetType: "grant", targetId: id,
            detail: new { hash = grant.TemplateHash, target = grant.TargetAgentId });
        await db.SaveChangesAsync(ct);
        grant.Status = GrantStatuses.Denied;
        Changed(grant);
        return Results.NoContent();
    }

    /// <summary>The owner or the grantee ends a requested or active grant at once, and the runs it approved are cancelled.</summary>
    public async Task<IResult> RevokeAsync(Guid id, HttpContext http)
    {
        var ct = http.RequestAborted;
        var userId = http.User.UserId();
        var now = clock.GetUtcNow();
        var grant = await db.MachineGrants.AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id && (g.OwnerUserId == userId || g.GranteeUserId == userId), ct);
        if (grant is null || await Access.MemberAsync(db, userId, grant.WorkspaceId, Roles.Member, ct) is null) return Http.NotFound();
        var changed = await db.MachineGrants
            .Where(g => g.Id == id && (g.Status == GrantStatuses.Active || g.Status == GrantStatuses.Requested))
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, GrantStatuses.Revoked).SetProperty(g => g.RevokedAt, now)
                .SetProperty(g => g.RevokedBy, userId), ct);
        if (changed != 1) return RemoteChecks.Conflict(RemoteErrors.NotPending);
        Audit.Add(db, http, clock, AuditActions.GrantRevoked, grant.WorkspaceId, userId, targetType: "grant", targetId: id,
            detail: new { hash = grant.TemplateHash, target = grant.TargetAgentId });
        await db.SaveChangesAsync(ct);
        grant.Status = GrantStatuses.Revoked;
        Changed(grant);
        await CancelByGrantAsync(db, broker, id, now, ct);
        return Results.NoContent();
    }

    /// <summary>Cancels the open runs a grant approved (a revoked grant approves nothing more).</summary>
    public static Task CancelByGrantAsync(MonitorDb db, Broker broker, Guid grantId, DateTimeOffset now, CancellationToken ct) =>
        RemoteChecks.CancelRunsAsync(db, broker, r => r.GrantId == grantId, now, ct);

    private void Changed(MachineGrant grant) => broker.Publish(Broker.Workspace(grant.WorkspaceId),
        new StreamMessage(WorkspaceEvent, new { id = grant.Id, status = grant.Status, targetAgentId = grant.TargetAgentId }));

    private IResult? Shape(int days, string? reason) => days < 1 || days > config.GrantDaysMax
        ? Http.Invalid("days", "out_of_range")
        : RemoteChecks.ReasonProblem(reason, config);

    private MachineGrant New(Guid workspaceId, Agent target, Guid granteeUserId, IReadOnlyList<string> template, string cwd, int timeout,
        int days, string? reason)
    {
        var now = clock.GetUtcNow();
        return new MachineGrant
        {
            WorkspaceId = workspaceId,
            TargetAgentId = target.Id,
            OwnerUserId = target.UserId,
            GranteeUserId = granteeUserId,
            Template = template.ToList(),
            Cwd = cwd,
            MaxTimeoutSeconds = timeout,
            TemplateHash = GrantMatcher.Hash(new GrantTemplate(template, cwd, timeout)),
            Reason = reason,
            CreatedAt = now,
            ExpiresAt = now.AddDays(days),
        };
    }

    private async Task<Target?> TargetAsync(Guid agentId, Guid workspaceId, CancellationToken ct)
    {
        var row = await (from a in db.Agents.AsNoTracking()
                         join m in db.Machines.AsNoTracking() on a.MachineId equals m.Id
                         where a.Id == agentId && a.WorkspaceId == workspaceId && a.Status == AgentStatuses.Active
                         select new { a, m.Os }).FirstOrDefaultAsync(ct);
        return row is null ? null : new Target(row.a, row.Os);
    }

    /// <summary>A grant of the target's owner, still requested or active.</summary>
    private async Task<MachineGrant?> OwnedAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var grant = await db.MachineGrants.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id && g.OwnerUserId == userId, ct);
        return grant is not null && await Access.MemberAsync(db, userId, grant.WorkspaceId, Roles.Member, ct) is not null ? grant : null;
    }

    /// <summary>The requested or active grant with the same target, grantee and template, if any.</summary>
    private Task<MachineGrant?> LiveAsync(MachineGrant grant, CancellationToken ct) => db.MachineGrants.AsNoTracking()
        .FirstOrDefaultAsync(g => g.TargetAgentId == grant.TargetAgentId && g.GranteeUserId == grant.GranteeUserId
                                  && g.TemplateHash == grant.TemplateHash
                                  && (g.Status == GrantStatuses.Requested || g.Status == GrantStatuses.Active), ct);

    /// <summary>Saves the grant with its audit row; when the unique key says an equal one is live, that one is returned instead (null if it ended meanwhile).</summary>
    private async Task<MachineGrant?> InsertAsync(MachineGrant grant, HttpContext http, string action, Guid? agentId, CancellationToken ct)
    {
        db.MachineGrants.Add(grant);
        Audit.Add(db, http, clock, action, grant.WorkspaceId, agentId is null ? grant.OwnerUserId : grant.GranteeUserId, agentId,
            targetType: "grant", targetId: grant.Id, detail: new { hash = grant.TemplateHash, target = grant.TargetAgentId });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return await LiveAsync(grant, ct);
        }

        Changed(grant);
        return grant;
    }
}
