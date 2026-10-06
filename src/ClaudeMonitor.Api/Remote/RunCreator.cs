using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ClaudeMonitor.Api.Remote;

/// <summary>The outcome of a run request: the run, or why it was refused (an HTTP status and an error code).</summary>
public sealed record RunCreateResult(RunCreated? Run, int Status, string? Error)
{
    public static RunCreateResult Refused(int status, string error) => new(null, status, error);
}

/// <summary>
/// A requester's agent asks for a run (ADR-0004). Fail-closed at every step: the workspace switch, the requester's
/// membership, the target's exec level and its owner's membership, the pending cap and the rate limit. A grant of the
/// requester (or an active job) approves it at once; anything else waits for the target's owner.
/// </summary>
public sealed class RunCreator(MonitorDb db, ApiConfig config, TimeProvider clock, Broker broker)
{
    private const string UniqueViolation = "23505";

    public async Task<RunCreateResult> CreateAsync(RunCreate req, Guid agentId, Guid userId, Guid workspaceId, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(req);
        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        var settings = await db.WorkspaceSettings.AsNoTracking().FirstAsync(s => s.WorkspaceId == workspaceId, ct);
        if (!settings.RemoteRunsEnabled) return RunCreateResult.Refused(StatusCodes.Status409Conflict, RemoteErrors.Disabled);
        if (await Access.MemberAsync(db, userId, workspaceId, Roles.Member, ct) is null) return NotFound();

        var target = await (from a in db.Agents.AsNoTracking()
                            join m in db.Machines.AsNoTracking() on a.MachineId equals m.Id
                            where a.Id == req.TargetAgentId && a.WorkspaceId == workspaceId && a.Status == AgentStatuses.Active
                            select new { a, m.Os }).FirstOrDefaultAsync(ct);
        if (target is null) return NotFound();
        if (RunRules.Check(req, config, target.Os) is { } invalid) return RunCreateResult.Refused(StatusCodes.Status400BadRequest, invalid);

        var existing = await db.RemoteRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RequesterAgentId == agentId && r.ClientKey == req.ClientKey, ct);
        if (existing is not null) return Again(req, existing);

        if (!ExecLevels.Allows(target.a.ExecLevel, req.Mode)
            || await Access.MemberAsync(db, target.a.UserId, workspaceId, Roles.Member, ct) is null)
        {
            return RunCreateResult.Refused(StatusCodes.Status409Conflict, RemoteErrors.TargetCannotRun);
        }

        if (await db.RemoteRuns.CountAsync(r => r.RequesterAgentId == agentId && r.CreatedAt > now.AddMinutes(-1), ct) >= config.RunCreatesPerMinute)
        {
            return RunCreateResult.Refused(StatusCodes.Status429TooManyRequests, RemoteErrors.TargetBusy);
        }

        var run = new RemoteRun
        {
            WorkspaceId = workspaceId,
            RequesterAgentId = agentId,
            RequesterUserId = userId,
            RequesterSessionId = await SessionAsync(agentId, req.SessionExternalId, ct),
            TargetAgentId = target.a.Id,
            TargetUserId = target.a.UserId,
            ClientKey = req.ClientKey,
            Mode = req.Mode,
            Argv = req.Argv?.ToList(),
            ShellCommand = req.ShellCommand,
            Cwd = req.Cwd,
            TimeoutSeconds = req.TimeoutSeconds,
            Reason = req.Reason,
            CreatedAt = now,
            ExpiresAt = now + config.RunPendingLifetime,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await LockAsync(workspaceId, userId, target.a.Id, target.a.UserId, req.Mode, ct) is { } refused) return refused;
        var approvedBy = await ApproveByJobAsync(req, run, now, ct) ?? await ApproveByGrantAsync(run, target.Os, now, ct);
        if (approvedBy is null
            && await db.RemoteRuns.CountAsync(r => r.TargetAgentId == run.TargetAgentId && r.Status == RunStatuses.PendingApproval, ct)
               >= config.RunPendingMaxPerTarget)
        {
            return RunCreateResult.Refused(StatusCodes.Status409Conflict, RemoteErrors.TargetBusy);
        }

        db.RemoteRuns.Add(run);
        Audit.Add(db, http, clock, AuditActions.RunCreated, workspaceId, userId, agentId, targetType: "run", targetId: run.Id,
            detail: new { mode = run.Mode, target = run.TargetAgentId, hash = RunHash(run), grant = run.GrantId, job = run.JobId, status = run.Status });
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var raced = await db.RemoteRuns.AsNoTracking().FirstAsync(r => r.RequesterAgentId == agentId && r.ClientKey == req.ClientKey, ct);
            return Again(req, raced);
        }

        RunNotices.Changed(broker, run);
        if (run.Status == RunStatuses.Approved) await RunNotices.DeliverAsync(broker, db, run, ct);
        return new RunCreateResult(new RunCreated(run.Id, run.Status, run.GrantId, run.ExpiresAt), StatusCodes.Status200OK, null);
    }

    public static string RunHash(RemoteRun run) =>
        GrantMatcher.RunHash(run.Mode, run.Argv, run.ShellCommand, run.Cwd, run.TimeoutSeconds, run.TargetAgentId);

    private static RunCreateResult NotFound() => RunCreateResult.Refused(StatusCodes.Status404NotFound, "not_found");

    private static RunCreateResult Again(RunCreate req, RemoteRun run) => RunRules.SameCommand(req, run)
        ? new RunCreateResult(new RunCreated(run.Id, run.Status, run.GrantId, run.ExpiresAt), StatusCodes.Status200OK, null)
        : RunCreateResult.Refused(StatusCodes.Status409Conflict, RemoteErrors.Mismatch);

    private async Task<Guid?> SessionAsync(Guid agentId, string? externalId, CancellationToken ct) => externalId is null
        ? null
        : await db.HarnessSessions.AsNoTracking().Where(s => s.AgentId == agentId && s.ExternalId == externalId)
            .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);

    /// <summary>An active job of this target with exactly this command, and no placeholder in it, approves the run.</summary>
    private async Task<Guid?> ApproveByJobAsync(RunCreate req, RemoteRun run, DateTimeOffset now, CancellationToken ct)
    {
        if (req.JobId is not { } jobId || run.Mode != RunModes.Argv) return null;
        var job = await db.MachineJobs.AsNoTracking().FirstOrDefaultAsync(
            j => j.Id == jobId && j.TargetAgentId == run.TargetAgentId && j.Status == JobStatuses.Active, ct);
        if (job is not { Argv: { } argv } || !argv.SequenceEqual(run.Argv ?? []) || job.Cwd != run.Cwd
            || run.TimeoutSeconds > job.TimeoutSeconds || argv.Any(a => a.Contains('{')))
        {
            return null;
        }

        // Locks the job row: a retire that comes later waits, then its sweep finds this run and cancels it.
        if (await db.MachineJobs.Where(j => j.Id == job.Id && j.Status == JobStatuses.Active)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, j => j.Status), ct) != 1)
        {
            return null;
        }

        run.JobId = job.Id;
        Approve(run, job.OwnerUserId, now);
        return job.OwnerUserId;
    }

    /// <summary>
    /// The first active grant of the requester that matches approves the run; its use is counted in the same transaction,
    /// conditional on it still being active, so a grant revoked a moment ago approves nothing.
    /// </summary>
    private async Task<Guid?> ApproveByGrantAsync(RemoteRun run, string os, DateTimeOffset now, CancellationToken ct)
    {
        if (run.Mode != RunModes.Argv || run.Argv is null) return null;
        var grants = await db.MachineGrants.AsNoTracking()
            .Where(g => g.TargetAgentId == run.TargetAgentId && g.GranteeUserId == run.RequesterUserId && g.Status == GrantStatuses.Active
                        && g.ExpiresAt > now && (g.GranteeAgentId == null || g.GranteeAgentId == run.RequesterAgentId))
            .ToListAsync(ct);
        foreach (var g in grants)
        {
            if (g is not { Template: { } template, Cwd: { } cwd }) continue;
            var grant = new GrantTemplate(template, cwd, g.MaxTimeoutSeconds);
            if (!GrantMatcher.Matches(grant, run.Argv, run.Cwd, run.TimeoutSeconds, os)) continue;
            var counted = await db.MachineGrants
                .Where(x => x.Id == g.Id && x.Status == GrantStatuses.Active && x.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UseCount, x => x.UseCount + 1).SetProperty(x => x.LastUsedAt, now), ct);
            if (counted != 1) continue;
            run.GrantId = g.Id;
            Approve(run, g.OwnerUserId, now);
            return g.OwnerUserId;
        }

        return null;
    }

    /// <summary>
    /// Re-checks, under row locks held until commit, what the reads above saw: the workspace switch, the target agent
    /// (active, here, its level) and both memberships. A switch-off, revoke, move or removal running at the same moment
    /// waits for this run to commit, and its sweep then cancels it.
    /// </summary>
    private async Task<RunCreateResult?> LockAsync(Guid workspaceId, Guid userId, Guid targetId, Guid ownerId, string mode, CancellationToken ct)
    {
        var levels = mode == RunModes.Shell ? new[] { ExecLevels.Shell } : new[] { ExecLevels.Argv, ExecLevels.Shell };
        if (await db.WorkspaceSettings.Where(s => s.WorkspaceId == workspaceId && s.RemoteRunsEnabled)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RemoteRunsEnabled, x => x.RemoteRunsEnabled), ct) != 1)
        {
            return RunCreateResult.Refused(StatusCodes.Status409Conflict, RemoteErrors.Disabled);
        }

        var members = await db.WorkspaceMembers
            .Where(m => m.WorkspaceId == workspaceId && (m.UserId == userId || m.UserId == ownerId) && m.RemovedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, m => m.Role), ct);
        var agent = await db.Agents
            .Where(a => a.Id == targetId && a.WorkspaceId == workspaceId && a.Status == AgentStatuses.Active && levels.Contains(a.ExecLevel))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ExecLevel, a => a.ExecLevel), ct);
        return members == (userId == ownerId ? 1 : 2) && agent == 1
            ? null
            : RunCreateResult.Refused(StatusCodes.Status409Conflict, RemoteErrors.TargetCannotRun);
    }

    private void Approve(RemoteRun run, Guid by, DateTimeOffset now)
    {
        run.Status = RunStatuses.Approved;
        run.DecidedAt = now;
        run.DecidedBy = by;
        run.ExpiresAt = RunNotices.StartBy(config, now);
    }
}
