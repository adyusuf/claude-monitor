using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Endpoints;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Api.Text;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ClaudeMonitor.Api.Remote;

/// <summary>The owner's approval of a job: Code is a TOTP or recovery code, needed when the sign-in is not recent.</summary>
public sealed record JobApproval(string? Code);

/// <summary>
/// Jobs as they are proposed and decided (ADR-0005). A requester's agent proposes; only the target agent's owner
/// approves (after a recent sign-in or a code), denies or retires. A job's command is frozen: changing it means
/// retiring and proposing again. Running one is an ordinary run request with its JobId. Every transition is a
/// compare-and-set, and the stream hears of it after the commit.
/// </summary>
public sealed class JobService(MonitorDb db, ApiConfig config, TimeProvider clock, Broker broker)
{
    public const string WorkspaceEvent = "job";
    public const string NameTaken = "job_name_taken";
    public const string InvalidName = "invalid_name";
    public const int NameMax = 100;
    private const string UniqueViolation = "23505";

    public async Task<IResult> ProposeAsync(JobProposal req, Guid agentId, Guid userId, Guid workspaceId, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(req);
        var ct = http.RequestAborted;
        var settings = await db.WorkspaceSettings.AsNoTracking().FirstAsync(s => s.WorkspaceId == workspaceId, ct);
        if (!settings.RemoteRunsEnabled) return RemoteChecks.Conflict(RemoteErrors.Disabled);
        if (await Access.MemberAsync(db, userId, workspaceId, Roles.Member, ct) is null) return Http.NotFound();
        var target = await (from a in db.Agents.AsNoTracking()
                            join m in db.Machines.AsNoTracking() on a.MachineId equals m.Id
                            where a.Id == req.TargetAgentId && a.WorkspaceId == workspaceId && a.Status == AgentStatuses.Active
                            select new { a, m.Os }).FirstOrDefaultAsync(ct);
        if (target is null) return Http.NotFound();

        var name = req.Name?.Trim();
        var search = SearchText.Normalize(name);
        if (name is not { Length: >= 1 and <= NameMax } || name.Any(char.IsControl) || search.Length == 0) return Http.Invalid("name", InvalidName);
        if (RemoteChecks.ReasonProblem(req.Reason, config) is { } reason) return reason;
        if (RemoteChecks.TemplateProblem(req.Argv, req.Cwd, req.TimeoutSeconds, target.Os, true) is { } bad) return bad;
        if (await LiveNameAsync(target.a.Id, search, ct)) return RemoteChecks.Conflict(NameTaken);
        if (await db.MachineJobs.CountAsync(j => j.TargetAgentId == target.a.Id && j.Status == JobStatuses.Proposed, ct)
            >= config.RunPendingMaxPerTarget)
        {
            return RemoteChecks.Conflict(RemoteErrors.TargetBusy);
        }

        var job = new MachineJob
        {
            WorkspaceId = workspaceId,
            TargetAgentId = target.a.Id,
            OwnerUserId = target.a.UserId,
            Name = name,
            NameSearch = search,
            Argv = req.Argv.ToList(),
            Cwd = req.Cwd,
            TimeoutSeconds = req.TimeoutSeconds,
            Reason = req.Reason,
            ProposedByUserId = userId,
            ProposedByAgentId = agentId,
            CreatedAt = clock.GetUtcNow(),
        };
        db.MachineJobs.Add(job);
        Audit.Add(db, http, clock, AuditActions.JobProposed, workspaceId, userId, agentId, targetType: "job", targetId: job.Id,
            detail: new { target = job.TargetAgentId });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return RemoteChecks.Conflict(NameTaken);
        }

        Changed(job);
        return Results.Ok(JobQueries.View(job));
    }

    public async Task<IResult> ApproveAsync(Guid id, JobApproval? req, HttpContext http)
    {
        var ct = http.RequestAborted;
        var userId = http.User.UserId();
        var now = clock.GetUtcNow();
        var job = await OwnedAsync(id, userId, ct);
        if (job is null) return Http.NotFound();
        if (job.Status != JobStatuses.Proposed) return RemoteChecks.Conflict(RemoteErrors.NotPending);
        if (!await RemoteChecks.ReauthAsync(http, db, config, clock, userId, req?.Code)) return RemoteChecks.ReauthRefused();

        var changed = await db.MachineJobs.Where(j => j.Id == id && j.Status == JobStatuses.Proposed && j.OwnerUserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatuses.Active).SetProperty(j => j.DecidedAt, now)
                .SetProperty(j => j.DecidedBy, userId), ct);
        if (changed != 1) return RemoteChecks.Conflict(RemoteErrors.NotPending);
        Audit.Add(db, http, clock, AuditActions.JobApproved, job.WorkspaceId, userId, targetType: "job", targetId: id,
            detail: new { target = job.TargetAgentId });
        await db.SaveChangesAsync(ct);
        job.Status = JobStatuses.Active;
        Changed(job);
        return Results.NoContent();
    }

    public async Task<IResult> DenyAsync(Guid id, HttpContext http)
    {
        var ct = http.RequestAborted;
        var userId = http.User.UserId();
        var now = clock.GetUtcNow();
        var job = await OwnedAsync(id, userId, ct);
        if (job is null) return Http.NotFound();
        var changed = await db.MachineJobs.Where(j => j.Id == id && j.Status == JobStatuses.Proposed && j.OwnerUserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatuses.Denied).SetProperty(j => j.DecidedAt, now)
                .SetProperty(j => j.DecidedBy, userId), ct);
        if (changed != 1) return RemoteChecks.Conflict(RemoteErrors.NotPending);
        Audit.Add(db, http, clock, AuditActions.JobDenied, job.WorkspaceId, userId, targetType: "job", targetId: id,
            detail: new { target = job.TargetAgentId });
        await db.SaveChangesAsync(ct);
        job.Status = JobStatuses.Denied;
        Changed(job);
        return Results.NoContent();
    }

    /// <summary>The owner retires a proposed or active job; the open runs it approved are cancelled.</summary>
    public async Task<IResult> RetireAsync(Guid id, HttpContext http)
    {
        var ct = http.RequestAborted;
        var userId = http.User.UserId();
        var now = clock.GetUtcNow();
        var job = await OwnedAsync(id, userId, ct);
        if (job is null) return Http.NotFound();
        var changed = await db.MachineJobs
            .Where(j => j.Id == id && j.OwnerUserId == userId && (j.Status == JobStatuses.Active || j.Status == JobStatuses.Proposed))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatuses.Retired).SetProperty(j => j.RetiredAt, now), ct);
        if (changed != 1) return RemoteChecks.Conflict(RemoteErrors.NotPending);
        Audit.Add(db, http, clock, AuditActions.JobRetired, job.WorkspaceId, userId, targetType: "job", targetId: id,
            detail: new { target = job.TargetAgentId });
        await db.SaveChangesAsync(ct);
        job.Status = JobStatuses.Retired;
        Changed(job);
        await CancelByJobAsync(db, broker, id, now, ct);
        return Results.NoContent();
    }

    /// <summary>Cancels the open runs a job approved (a retired job approves nothing more).</summary>
    public static Task CancelByJobAsync(MonitorDb db, Broker broker, Guid jobId, DateTimeOffset now, CancellationToken ct) =>
        RemoteChecks.CancelRunsAsync(db, broker, r => r.JobId == jobId, now, ct);

    private void Changed(MachineJob job) => broker.Publish(Broker.Workspace(job.WorkspaceId),
        new StreamMessage(WorkspaceEvent, new { id = job.Id, status = job.Status, targetAgentId = job.TargetAgentId }));

    private Task<bool> LiveNameAsync(Guid targetAgentId, string nameSearch, CancellationToken ct) => db.MachineJobs.AsNoTracking()
        .AnyAsync(j => j.TargetAgentId == targetAgentId && j.NameSearch == nameSearch
                       && (j.Status == JobStatuses.Proposed || j.Status == JobStatuses.Active), ct);

    /// <summary>A job of the target's owner, who must still be a member.</summary>
    private async Task<MachineJob?> OwnedAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var job = await db.MachineJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id && j.OwnerUserId == userId, ct);
        return job is not null && await Access.MemberAsync(db, userId, job.WorkspaceId, Roles.Member, ct) is not null ? job : null;
    }
}
