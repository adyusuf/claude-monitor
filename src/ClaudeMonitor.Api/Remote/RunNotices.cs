using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// What the streams hear about a run, always after the change is committed: the workspace's pages ("run"), the
/// requester's agent (run_update) and, for an approved run, the target (run) or a cancel (run_cancel).
/// </summary>
public static class RunNotices
{
    public const string WorkspaceEvent = "run";

    public static void Changed(Broker broker, RemoteRun run)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(run);
        broker.Publish(Broker.Workspace(run.WorkspaceId),
            new StreamMessage(WorkspaceEvent, new { id = run.Id, status = run.Status, targetAgentId = run.TargetAgentId }));
        broker.Publish(Broker.Agent(run.RequesterAgentId),
            new StreamMessage(AgentStreamEvents.RunUpdate, new RunUpdateMessage(run.Id, run.Status)));
    }

    public static async Task DeliverAsync(Broker broker, MonitorDb db, RemoteRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(broker);
        if (await RoleBlocksAsync(db, broker, run, ct)) return;
        broker.Publish(Broker.Agent(run.TargetAgentId), new StreamMessage(AgentStreamEvents.Run, await MessageAsync(db, run, ct)));
    }

    /// <summary>
    /// The role is checked again at delivery (ADR-0008 §3c): a script, shell command or interpreter run approved without a two-step code whose
    /// target has become strict since is cancelled with <c>role_changed</c> and never delivered. True when it was cancelled.
    /// </summary>
    public static async Task<bool> RoleBlocksAsync(MonitorDb db, Broker broker, RemoteRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(run);
        if (run.ApprovedWithTotp || run.Mode is not (RunModes.Script or RunModes.Shell or RunModes.Argv)) return false;
        var row = await (from a in db.Agents.AsNoTracking()
                         join m in db.Machines.AsNoTracking() on a.MachineId equals m.Id
                         where a.Id == run.TargetAgentId
                         select new { a, m.Os }).FirstOrDefaultAsync(ct);
        if (row is null || !RunCodeRule.NeedsCode(run.Mode, run.Argv, row.Os, MachineRoles.IsStrict(
                MachineRoles.EffectiveEnvironment(row.a.RoleEnvironment, row.a.ClaimedEnvironment),
                MachineRoles.EffectiveFunction(row.a.RoleFunction, row.a.ClaimedFunction))))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var changed = await db.RemoteRuns.Where(r => r.Id == run.Id && (r.Status == RunStatuses.Approved || r.Status == RunStatuses.Delivered))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatuses.Cancelled).SetProperty(r => r.EndedAt, now)
                .SetProperty(r => r.Error, RemoteErrors.RoleChanged), ct);
        if (changed == 1)
        {
            run.Status = RunStatuses.Cancelled;
            Changed(broker, run);
        }

        return true;
    }

    public static void Cancel(Broker broker, RemoteRun run)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(run);
        broker.Publish(Broker.Agent(run.TargetAgentId), new StreamMessage(AgentStreamEvents.RunCancel, new RunCancelMessage(run.Id)));
    }

    /// <summary>The run as its target receives it, with the grant it matched so the target can check it again.</summary>
    public static async Task<RunMessage> MessageAsync(MonitorDb db, RemoteRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(run);
        GrantTemplate? grant = null;
        if (run.GrantId is { } grantId)
        {
            var g = await db.MachineGrants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == grantId, ct);
            grant = g is { Template: { } template, Cwd: { } cwd } ? new GrantTemplate(template, cwd, g.MaxTimeoutSeconds) : null;
        }

        return new RunMessage(run.Id, run.Mode, run.Argv, run.ShellCommand, run.Cwd, run.TimeoutSeconds, run.ExpiresAt,
            run.GrantId, grant, run.DecidedAt, run.ScriptInterpreter, run.ScriptBody, run.ScriptArgs, run.OutputCap, run.ApprovedWithTotp, run.ScriptFiles, run.ScriptStdin);
    }

    /// <summary>The approved runs a target still owes an answer for, replayed on every stream connection.</summary>
    public static async Task<List<RunMessage>> WaitingForAsync(MonitorDb db, Guid targetAgentId, DateTimeOffset now, CancellationToken ct, Broker? broker = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var runs = await db.RemoteRuns.AsNoTracking()
            .Where(r => r.TargetAgentId == targetAgentId && (r.Status == RunStatuses.Approved || r.Status == RunStatuses.Delivered)
                        && r.ExpiresAt > now)
            .OrderBy(r => r.CreatedAt).Take(50).ToListAsync(ct);
        var messages = new List<RunMessage>(runs.Count);
        foreach (var run in runs)
        {
            if (broker is not null && await RoleBlocksAsync(db, broker, run, ct)) continue;
            messages.Add(await MessageAsync(db, run, ct));
        }

        return messages;
    }

    /// <summary>A not_after the target enforces: an approved run that has not started by then never starts.</summary>
    public static DateTimeOffset StartBy(ApiConfig config, DateTimeOffset now) =>
        now + (config ?? throw new ArgumentNullException(nameof(config))).RunStartWindow;
}
