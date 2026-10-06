using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Endpoints;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

public sealed record RunApproval(string? Hash, string? Code);

public sealed record RunDenial(string? Reason);

/// <summary>
/// The owner's answer and the cancels (ADR-0005). Every transition is a compare-and-set on the run's status, so an
/// approval racing the Housekeeper's expiry, or two clicks, change it once; the loser gets 409.
/// </summary>
public sealed class RunDecisions(MonitorDb db, ApiConfig config, TimeProvider clock, Broker broker)
{
    private static readonly string[] Cancellable =
        [RunStatuses.PendingApproval, RunStatuses.Approved, RunStatuses.Delivered, RunStatuses.Running];

    /// <summary>Only the target agent's owner, still a member, with the hash of the run they saw; TOTP for shell when they have it on.</summary>
    public async Task<IResult> ApproveAsync(Guid id, RunApproval req, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(req);
        var ct = http.RequestAborted;
        var userId = http.User.UserId();
        var run = await OwnedAsync(id, userId, ct);
        if (run is null) return Http.NotFound();
        if (run.Status != RunStatuses.PendingApproval) return Conflict(RemoteErrors.NotPending);
        if (!string.Equals(req.Hash, RunCreator.RunHash(run), StringComparison.Ordinal)) return Conflict(RemoteErrors.Mismatch);
        var settings = await db.WorkspaceSettings.AsNoTracking().FirstAsync(s => s.WorkspaceId == run.WorkspaceId, ct);
        var target = await db.Agents.AsNoTracking().FirstAsync(a => a.Id == run.TargetAgentId, ct);
        if (!settings.RemoteRunsEnabled) return Conflict(RemoteErrors.Disabled);
        if (target.Status != AgentStatuses.Active || target.WorkspaceId != run.WorkspaceId || !ExecLevels.Allows(target.ExecLevel, run.Mode))
        {
            return Conflict(RemoteErrors.TargetCannotRun);
        }

        var now = clock.GetUtcNow();
        if (run.Mode == RunModes.Shell)
        {
            var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
            if (user.TotpEnabledAt is not null && !await MfaEndpoints.CheckAsync(db, config, user, req.Code, now, ct))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: RemoteErrors.MfaRequired);
            }
        }

        var startBy = RunNotices.StartBy(config, now);
        var changed = await db.RemoteRuns
            .Where(r => r.Id == id && r.Status == RunStatuses.PendingApproval && r.ExpiresAt > now && r.TargetUserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatuses.Approved).SetProperty(r => r.DecidedAt, now)
                .SetProperty(r => r.DecidedBy, userId).SetProperty(r => r.ExpiresAt, startBy), ct);
        if (changed != 1) return Conflict(RemoteErrors.NotPending);
        Audit.Add(db, http, clock, AuditActions.RunApproved, run.WorkspaceId, userId, targetType: "run", targetId: id,
            detail: new { hash = req.Hash, mode = run.Mode });
        await db.SaveChangesAsync(ct);
        run.Status = RunStatuses.Approved;
        run.DecidedAt = now;
        run.ExpiresAt = startBy;
        RunNotices.Changed(broker, run);
        await RunNotices.DeliverAsync(broker, db, run, ct);
        return Results.NoContent();
    }

    public async Task<IResult> DenyAsync(Guid id, RunDenial req, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(req);
        var ct = http.RequestAborted;
        var userId = http.User.UserId();
        var run = await OwnedAsync(id, userId, ct);
        if (run is null) return Http.NotFound();
        var now = clock.GetUtcNow();
        var error = req.Reason is { Length: > 0 } reason ? reason[..Math.Min(reason.Length, config.RunReasonMax)] : null;
        var changed = await db.RemoteRuns
            .Where(r => r.Id == id && r.Status == RunStatuses.PendingApproval && r.TargetUserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatuses.Denied).SetProperty(r => r.DecidedAt, now)
                .SetProperty(r => r.DecidedBy, userId).SetProperty(r => r.EndedAt, now).SetProperty(r => r.Error, error), ct);
        if (changed != 1) return Conflict(RemoteErrors.NotPending);
        Audit.Add(db, http, clock, AuditActions.RunDenied, run.WorkspaceId, userId, targetType: "run", targetId: id);
        await db.SaveChangesAsync(ct);
        run.Status = RunStatuses.Denied;
        RunNotices.Changed(broker, run);
        return Results.NoContent();
    }

    /// <summary>The requester (any of their agents) or the owner may cancel a run that has not finished.</summary>
    public async Task<IResult> CancelAsync(Guid id, Guid userId, Guid? agentId, HttpContext http)
    {
        var ct = http.RequestAborted;
        var run = await db.RemoteRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null || (run.RequesterUserId != userId && run.TargetUserId != userId)
            || await Access.MemberAsync(db, userId, run.WorkspaceId, Roles.Member, ct) is null)
        {
            return Http.NotFound();
        }

        var now = clock.GetUtcNow();
        var changed = await db.RemoteRuns.Where(r => r.Id == id && Cancellable.Contains(r.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatuses.Cancelled).SetProperty(r => r.EndedAt, now), ct);
        if (changed != 1) return Conflict(RemoteErrors.NotPending);
        Audit.Add(db, http, clock, AuditActions.RunCancelled, run.WorkspaceId, userId, agentId, targetType: "run", targetId: id);
        await db.SaveChangesAsync(ct);
        var was = run.Status;
        run.Status = RunStatuses.Cancelled;
        RunNotices.Changed(broker, run);
        if (was != RunStatuses.PendingApproval) RunNotices.Cancel(broker, run);
        return Results.NoContent();
    }

    /// <summary>
    /// Cancels every open run of a workspace, or of one agent as requester or target (member removed, agent moved, switch off).
    /// Inside a caller's transaction the stream notices go to <paramref name="after"/>, to be published once it commits.
    /// </summary>
    public static async Task CancelOpenAsync(MonitorDb db, Broker broker, DateTimeOffset now, Guid? workspaceId, Guid? agentId,
        Guid? userId, CancellationToken ct, List<Action>? after = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(broker);
        var runs = await db.RemoteRuns.AsNoTracking()
            .Where(r => Cancellable.Contains(r.Status)
                        && (workspaceId == null || r.WorkspaceId == workspaceId)
                        && (agentId == null || r.TargetAgentId == agentId || r.RequesterAgentId == agentId)
                        && (userId == null || r.TargetUserId == userId || r.RequesterUserId == userId))
            .ToListAsync(ct);
        foreach (var run in runs)
        {
            var changed = await db.RemoteRuns.Where(r => r.Id == run.Id && r.Status == run.Status)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatuses.Cancelled).SetProperty(r => r.EndedAt, now), ct);
            if (changed != 1) continue;
            var was = run.Status;
            run.Status = RunStatuses.Cancelled;
            void Notify()
            {
                RunNotices.Changed(broker, run);
                if (was != RunStatuses.PendingApproval) RunNotices.Cancel(broker, run);
            }

            if (after is null) Notify();
            else after.Add(Notify);
        }
    }

    private async Task<RemoteRun?> OwnedAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var run = await db.RemoteRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.TargetUserId == userId, ct);
        return run is not null && await Access.MemberAsync(db, userId, run.WorkspaceId, Roles.Member, ct) is not null ? run : null;
    }

    private static IResult Conflict(string error) => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: error);
}
