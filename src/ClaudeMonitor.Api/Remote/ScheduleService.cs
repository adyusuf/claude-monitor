using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Endpoints;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// The web's actions on job schedules (ADR-0010 §3). Only the owner of the job's target creates a schedule or starts a paused one (a recent sign-in or a code, like
/// a grant); the owner or a workspace admin can stop one at once. Never for a strict target, never for a job with a free placeholder, never while the workspace's
/// schedule switch is off, and at most <see cref="ScheduleLimits.PerTargetMax"/> a target.
/// </summary>
public sealed class ScheduleService(MonitorDb db, ApiConfig config, TimeProvider clock, Broker broker)
{
    public async Task<IResult> CreateAsync(ScheduleCreate req, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(req);
        var ct = http.RequestAborted;
        var userId = http.User.UserId();
        var now = clock.GetUtcNow();
        var job = await db.MachineJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == req.JobId && j.OwnerUserId == userId, ct);
        if (job is null || await Access.MemberAsync(db, userId, job.WorkspaceId, Roles.Member, ct) is null) return Http.NotFound();
        if (job.Status != JobStatuses.Active) return RemoteChecks.Conflict(RemoteErrors.NotPending);
        var next = ScheduleLimits.Next(req.Kind, req.IntervalMinutes, req.DailyAtMinutes, now);
        if (next is null) return Http.Invalid("kind", RemoteErrors.InvalidSchedule);
        if (job.Argv is not { } argv || argv.Any(a => a.Contains('{'))) return RemoteChecks.Conflict(RemoteErrors.SchedulePlaceholder);
        var settings = await db.WorkspaceSettings.AsNoTracking().FirstAsync(s => s.WorkspaceId == job.WorkspaceId, ct);
        if (!settings.RemoteRunsEnabled || !settings.RemoteSchedulesEnabled) return RemoteChecks.Conflict(RemoteErrors.SchedulesDisabled);
        var target = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == job.TargetAgentId && a.Status == AgentStatuses.Active, ct);
        if (target is null || !ExecLevels.Allows(target.ExecLevel, RunModes.Argv)) return RemoteChecks.Conflict(RemoteErrors.TargetCannotRun);
        if (MachineRoles.IsStrict(MachineRoles.EffectiveEnvironment(target.RoleEnvironment, target.ClaimedEnvironment), MachineRoles.EffectiveFunction(target.RoleFunction, target.ClaimedFunction)))
        {
            return RemoteChecks.Conflict(RemoteErrors.ScheduleStrict);
        }

        if (await db.JobSchedules.CountAsync(s => s.TargetAgentId == target.Id && s.Status != ScheduleStatuses.Ended, ct) >= ScheduleLimits.PerTargetMax)
        {
            return RemoteChecks.Conflict(RemoteErrors.ScheduleLimit);
        }

        if (await RemoteChecks.ApprovalGateAsync(http, db, config, clock, userId, target, req.Code) is { } refused) return refused;

        var schedule = new JobSchedule
        {
            WorkspaceId = job.WorkspaceId,
            JobId = job.Id,
            TargetAgentId = target.Id,
            OwnerUserId = userId,
            Kind = req.Kind,
            IntervalMinutes = req.IntervalMinutes,
            DailyAtMinutes = req.DailyAtMinutes,
            NextRunAt = next.Value,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.JobSchedules.Add(schedule);
        Audit.Add(db, http, clock, AuditActions.ScheduleCreated, job.WorkspaceId, userId, targetType: "schedule", targetId: schedule.Id,
            detail: new { job = job.Id, kind = req.Kind, interval = req.IntervalMinutes, daily = req.DailyAtMinutes });
        await db.SaveChangesAsync(ct);
        Changed(schedule);
        return Results.Ok((await ScheduleQueries.ForTargetAsync(db, target.Id, userId, schedule.Id, ct))[0]);
    }

    /// <summary>The owner or a workspace admin stops a schedule at once; it stays listed as paused until the owner starts it again or ends it.</summary>
    public async Task<IResult> PauseAsync(Guid id, HttpContext http)
    {
        var ct = http.RequestAborted;
        var userId = http.User.UserId();
        var schedule = await ControlledAsync(id, userId, ct);
        if (schedule is null) return Http.NotFound();
        var changed = await db.JobSchedules.Where(s => s.Id == id && s.Status == ScheduleStatuses.Active)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, ScheduleStatuses.Paused).SetProperty(s => s.PauseReason, ScheduleReasons.Owner).SetProperty(s => s.UpdatedAt, clock.GetUtcNow()), ct);
        if (changed != 1) return RemoteChecks.Conflict(RemoteErrors.NotPending);
        Audit.Add(db, http, clock, AuditActions.SchedulePaused, schedule.WorkspaceId, userId, targetType: "schedule", targetId: id, detail: new { reason = ScheduleReasons.Owner });
        await db.SaveChangesAsync(ct);
        Changed(schedule);
        return Results.NoContent();
    }

    /// <summary>The owner starts a paused schedule again, after the same confirmation as creating it; its failure count starts over and the next tick is counted from now.</summary>
    public async Task<IResult> ResumeAsync(Guid id, ScheduleResume? req, HttpContext http)
    {
        var ct = http.RequestAborted;
        var userId = http.User.UserId();
        var now = clock.GetUtcNow();
        var schedule = await db.JobSchedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.OwnerUserId == userId, ct);
        if (schedule is null || await Access.MemberAsync(db, userId, schedule.WorkspaceId, Roles.Member, ct) is null) return Http.NotFound();
        if (schedule.Status != ScheduleStatuses.Paused) return RemoteChecks.Conflict(RemoteErrors.NotPending);
        var job = await db.MachineJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == schedule.JobId, ct);
        var target = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == schedule.TargetAgentId && a.Status == AgentStatuses.Active, ct);
        var settings = await db.WorkspaceSettings.AsNoTracking().FirstAsync(s => s.WorkspaceId == schedule.WorkspaceId, ct);
        if (job is not { Status: JobStatuses.Active } || target is null || !ExecLevels.Allows(target.ExecLevel, RunModes.Argv)) return RemoteChecks.Conflict(RemoteErrors.TargetCannotRun);
        if (!settings.RemoteRunsEnabled || !settings.RemoteSchedulesEnabled) return RemoteChecks.Conflict(RemoteErrors.SchedulesDisabled);
        if (MachineRoles.IsStrict(MachineRoles.EffectiveEnvironment(target.RoleEnvironment, target.ClaimedEnvironment), MachineRoles.EffectiveFunction(target.RoleFunction, target.ClaimedFunction)))
        {
            return RemoteChecks.Conflict(RemoteErrors.ScheduleStrict);
        }

        if (await RemoteChecks.ApprovalGateAsync(http, db, config, clock, userId, target, req?.Code) is { } refused) return refused;
        var next = ScheduleLimits.Next(schedule.Kind, schedule.IntervalMinutes, schedule.DailyAtMinutes, now);
        if (next is null) return Http.Invalid("kind", RemoteErrors.InvalidSchedule);
        var changed = await db.JobSchedules.Where(s => s.Id == id && s.Status == ScheduleStatuses.Paused)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, ScheduleStatuses.Active).SetProperty(s => s.PauseReason, (string?)null).SetProperty(s => s.FailuresInARow, 0)
                .SetProperty(s => s.NextRunAt, next.Value).SetProperty(s => s.UpdatedAt, now), ct);
        if (changed != 1) return RemoteChecks.Conflict(RemoteErrors.NotPending);
        Audit.Add(db, http, clock, AuditActions.ScheduleResumed, schedule.WorkspaceId, userId, targetType: "schedule", targetId: id);
        await db.SaveChangesAsync(ct);
        Changed(schedule);
        return Results.NoContent();
    }

    public async Task<IResult> EndAsync(Guid id, HttpContext http)
    {
        var ct = http.RequestAborted;
        var userId = http.User.UserId();
        var schedule = await ControlledAsync(id, userId, ct);
        if (schedule is null) return Http.NotFound();
        var changed = await db.JobSchedules.Where(s => s.Id == id && s.Status != ScheduleStatuses.Ended)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, ScheduleStatuses.Ended).SetProperty(s => s.PauseReason, ScheduleReasons.Owner).SetProperty(s => s.UpdatedAt, clock.GetUtcNow()), ct);
        if (changed != 1) return RemoteChecks.Conflict(RemoteErrors.NotPending);
        Audit.Add(db, http, clock, AuditActions.ScheduleEnded, schedule.WorkspaceId, userId, targetType: "schedule", targetId: id, detail: new { reason = ScheduleReasons.Owner });
        await db.SaveChangesAsync(ct);
        Changed(schedule);
        return Results.NoContent();
    }

    /// <summary>A schedule the user may stop: its owner, or an admin of its workspace (a safety stop needs no confirmation).</summary>
    private async Task<JobSchedule?> ControlledAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var schedule = await db.JobSchedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (schedule is null) return null;
        var minimum = schedule.OwnerUserId == userId ? Roles.Member : Roles.Admin;
        return await Access.MemberAsync(db, userId, schedule.WorkspaceId, minimum, ct) is not null ? schedule : null;
    }

    private void Changed(JobSchedule schedule) => broker.Publish(Broker.Workspace(schedule.WorkspaceId),
        new StreamMessage("schedule", new { id = schedule.Id, targetAgentId = schedule.TargetAgentId }));
}

public static class ScheduleQueries
{
    public const int Max = 100;

    /// <summary>The schedules of one target, newest first, with the job's name and the status of the last run.</summary>
    public static async Task<List<ScheduleView>> ForTargetAsync(MonitorDb db, Guid targetAgentId, Guid userId, Guid? scheduleId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await (from s in db.JobSchedules.AsNoTracking()
                          join j in db.MachineJobs.AsNoTracking() on s.JobId equals j.Id
                          where s.TargetAgentId == targetAgentId && (scheduleId == null || s.Id == scheduleId)
                          orderby s.CreatedAt descending, s.Id descending
                          select new { s, j.Name }).Take(Max).ToListAsync(ct);
        var runIds = rows.Where(r => r.s.LastRunId != null).Select(r => r.s.LastRunId!.Value).ToList();
        var statuses = await db.RemoteRuns.AsNoTracking().Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Status, ct);
        return rows.Select(r => new ScheduleView(r.s.Id, r.s.JobId, r.Name, r.s.TargetAgentId, r.s.Kind, r.s.IntervalMinutes, r.s.DailyAtMinutes, r.s.Status, r.s.PauseReason,
            r.s.Status == ScheduleStatuses.Active ? r.s.NextRunAt : null, r.s.LastRunAt, r.s.LastRunId is { } id ? statuses.GetValueOrDefault(id) : null, r.s.FailuresInARow,
            r.s.OwnerUserId, r.s.OwnerUserId == userId, r.s.CreatedAt)).ToList();
    }
}
