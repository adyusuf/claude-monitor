using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Streaming;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Podium.Api.Remote;

/// <summary>
/// The engine of job schedules (ADR-0010 §3), run by the housekeeping every minute. A tick is claimed by a compare-and-set on <c>next_run_at</c>, so two API
/// instances never fire one tick twice, and then becomes an ordinary run the job approves. There is no catch-up: the next tick is counted from now. A schedule never
/// fires while its last run is still open, pauses itself after <see cref="ScheduleLimits.FailuresToPause"/> failed runs in a row, and stops (paused or ended)
/// when anything that made it safe stops being true: the job, the target's level, the target not being strict, the owner's membership.
/// </summary>
public static class JobSchedules
{
    public const int Batch = 100;

    // An array, not the contract's set: EF Core translates Contains on an array (IN).
    private static readonly string[] OpenStatuses = [RunStatuses.PendingApproval, RunStatuses.Approved, RunStatuses.Delivered, RunStatuses.Running];
    private static readonly string[] Counted = [RunStatuses.Failed, RunStatuses.TimedOut, RunStatuses.Expired];

    public static async Task FireDueAsync(PodiumDb db, ApiConfig config, Broker broker, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(broker);
        await SettleAsync(db, now, ct);
        var due = await db.JobSchedules.AsNoTracking().Where(s => s.Status == ScheduleStatuses.Active && s.NextRunAt <= now)
            .OrderBy(s => s.NextRunAt).ThenBy(s => s.Id).Take(Batch).ToListAsync(ct);
        foreach (var schedule in due)
        {
            await FireAsync(db, config, broker, schedule, now, ct);
        }
    }

    /// <summary>Counts the outcome of each schedule's last run once it is final: a success clears the failures, a failed, timed-out or expired run adds one, and the third pauses the schedule.</summary>
    private static async Task SettleAsync(PodiumDb db, DateTimeOffset now, CancellationToken ct)
    {
        var open = await db.JobSchedules.AsNoTracking().Where(s => !s.LastRunSettled && s.LastRunId != null && s.Status != ScheduleStatuses.Ended)
            .OrderBy(s => s.Id).Take(Batch).ToListAsync(ct);
        foreach (var schedule in open)
        {
            var status = await db.RemoteRuns.AsNoTracking().Where(r => r.Id == schedule.LastRunId).Select(r => r.Status).FirstOrDefaultAsync(ct);
            if (status is null || !RunStatuses.Final.Contains(status))
            {
                if (status is null) await db.JobSchedules.Where(s => s.Id == schedule.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.LastRunSettled, true), ct); // the run is gone
                continue;
            }

            var failures = status == RunStatuses.Succeeded ? 0 : Counted.Contains(status) ? schedule.FailuresInARow + 1 : schedule.FailuresInARow;
            var pause = failures >= ScheduleLimits.FailuresToPause && schedule.Status == ScheduleStatuses.Active;
            await db.JobSchedules.Where(s => s.Id == schedule.Id && !s.LastRunSettled)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastRunSettled, true).SetProperty(s => s.FailuresInARow, failures)
                    .SetProperty(s => s.Status, pause ? ScheduleStatuses.Paused : schedule.Status)
                    .SetProperty(s => s.PauseReason, pause ? ScheduleReasons.Failures : schedule.PauseReason).SetProperty(s => s.UpdatedAt, now), ct);
            if (pause) await AuditAsync(db, AuditActions.SchedulePaused, schedule, ScheduleReasons.Failures, now, ct);
        }
    }

    private static async Task FireAsync(PodiumDb db, ApiConfig config, Broker broker, JobSchedule schedule, DateTimeOffset now, CancellationToken ct)
    {
        var settings = await db.WorkspaceSettings.AsNoTracking().FirstOrDefaultAsync(s => s.WorkspaceId == schedule.WorkspaceId, ct);
        var job = await db.MachineJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == schedule.JobId, ct);
        var target = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == schedule.TargetAgentId, ct);
        if (job is not { Status: JobStatuses.Active, Argv: { } argv } || argv.Any(a => a.Contains('{'))) { await StopAsync(db, schedule, ScheduleStatuses.Ended, ScheduleReasons.JobRetired, now, ct); return; }
        if (target is null || target.Status != AgentStatuses.Active || target.WorkspaceId != schedule.WorkspaceId
            || await Access.MemberAsync(db, schedule.OwnerUserId, schedule.WorkspaceId, Roles.Member, ct) is null)
        {
            await StopAsync(db, schedule, ScheduleStatuses.Ended, ScheduleReasons.TargetGone, now, ct);
            return;
        }

        if (MachineRoles.IsStrict(MachineRoles.EffectiveEnvironment(target.RoleEnvironment, target.ClaimedEnvironment), MachineRoles.EffectiveFunction(target.RoleFunction, target.ClaimedFunction)))
        {
            await StopAsync(db, schedule, ScheduleStatuses.Paused, ScheduleReasons.Strict, now, ct); // no unattended runs on production or a database
            return;
        }

        if (target.ApprovalsRequired) { await StopAsync(db, schedule, ScheduleStatuses.Paused, ScheduleReasons.Approvals, now, ct); return; }
        if (!ExecLevels.Allows(target.ExecLevel, RunModes.Argv)) { await StopAsync(db, schedule, ScheduleStatuses.Paused, ScheduleReasons.ExecLevel, now, ct); return; }

        var next = ScheduleLimits.Next(schedule.Kind, schedule.IntervalMinutes, schedule.DailyAtMinutes, now);
        if (next is null) { await StopAsync(db, schedule, ScheduleStatuses.Ended, ScheduleReasons.Owner, now, ct); return; }
        var tick = schedule.NextRunAt;

        // The switch is off, the last run is still open, or the target already has its hands full: this tick is skipped, the schedule stays as it is.
        var skip = settings is not { RemoteRunsEnabled: true, RemoteSchedulesEnabled: true }
                   || (!schedule.LastRunSettled && schedule.LastRunId is { } last && await IsOpenAsync(db, last, ct))
                   || await db.RemoteRuns.CountAsync(r => r.TargetAgentId == schedule.TargetAgentId && OpenStatuses.Contains(r.Status), ct) >= config.RunPendingMaxPerTarget;
        var claimed = await db.JobSchedules.Where(s => s.Id == schedule.Id && s.Status == ScheduleStatuses.Active && s.NextRunAt == tick)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.NextRunAt, next.Value).SetProperty(s => s.UpdatedAt, now), ct);
        if (claimed != 1 || skip) return;

        var run = new RemoteRun
        {
            WorkspaceId = schedule.WorkspaceId,
            RequesterAgentId = target.Id,
            RequesterUserId = schedule.OwnerUserId,
            TargetAgentId = target.Id,
            TargetUserId = target.UserId,
            ClientKey = $"schedule:{schedule.Id:N}:{tick.ToUnixTimeSeconds()}",
            Mode = RunModes.Argv,
            Argv = [.. argv],
            Cwd = job.Cwd,
            TimeoutSeconds = job.TimeoutSeconds,
            Reason = "scheduled",
            JobId = job.Id,
            ScheduleId = schedule.Id,
            CreatedAt = now,
            Status = RunStatuses.Approved,
            DecidedAt = now,
            DecidedBy = job.OwnerUserId,
            ExpiresAt = RunNotices.StartBy(config, now),
        };
        db.RemoteRuns.Add(run);
        Audit.Add(db, null, TimeProvider.System, AuditActions.RunCreated, schedule.WorkspaceId, schedule.OwnerUserId, target.Id, targetType: "run", targetId: run.Id,
            detail: new { mode = run.Mode, target = run.TargetAgentId, hash = RunCreator.RunHash(run), job = job.Id, schedule = schedule.Id, status = run.Status });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" })
        {
            db.ChangeTracker.Clear(); // this tick already has its run
            return;
        }

        await db.JobSchedules.Where(s => s.Id == schedule.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastRunAt, now).SetProperty(s => s.LastRunId, run.Id).SetProperty(s => s.LastRunSettled, false), ct);
        RunNotices.Changed(broker, run);
        await RunNotices.DeliverAsync(broker, db, run, ct);
    }

    private static async Task<bool> IsOpenAsync(PodiumDb db, Guid runId, CancellationToken ct)
    {
        var status = await db.RemoteRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).FirstOrDefaultAsync(ct);
        return status is not null && !RunStatuses.Final.Contains(status);
    }

    /// <summary>Moves an active schedule to paused or ended, once (a compare-and-set on its status), and records why.</summary>
    public static async Task StopAsync(PodiumDb db, JobSchedule schedule, string status, string reason, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(schedule);
        var changed = await db.JobSchedules.Where(s => s.Id == schedule.Id && s.Status == ScheduleStatuses.Active)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, status).SetProperty(s => s.PauseReason, reason).SetProperty(s => s.UpdatedAt, now), ct);
        if (changed == 1) await AuditAsync(db, status == ScheduleStatuses.Ended ? AuditActions.ScheduleEnded : AuditActions.SchedulePaused, schedule, reason, now, ct);
    }

    /// <summary>Ends every live schedule of a job (it was retired): none of them fires again.</summary>
    public static async Task EndForJobAsync(PodiumDb db, Guid jobId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var live = await db.JobSchedules.AsNoTracking().Where(s => s.JobId == jobId && s.Status != ScheduleStatuses.Ended).OrderBy(s => s.Id).ToListAsync(ct);
        foreach (var schedule in live)
        {
            var changed = await db.JobSchedules.Where(s => s.Id == schedule.Id && s.Status != ScheduleStatuses.Ended)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, ScheduleStatuses.Ended).SetProperty(s => s.PauseReason, reason).SetProperty(s => s.UpdatedAt, now), ct);
            if (changed == 1) await AuditAsync(db, AuditActions.ScheduleEnded, schedule, reason, now, ct);
        }
    }

    private static async Task AuditAsync(PodiumDb db, string action, JobSchedule schedule, string reason, DateTimeOffset now, CancellationToken ct)
    {
        _ = now;
        Audit.Add(db, null, TimeProvider.System, action, schedule.WorkspaceId, schedule.OwnerUserId, targetType: "schedule", targetId: schedule.Id, detail: new { reason, job = schedule.JobId });
        await db.SaveChangesAsync(ct);
    }
}
