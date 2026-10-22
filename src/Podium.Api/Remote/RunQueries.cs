using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Remote;

/// <summary>
/// A run as the web shows it. Command and output only for the requester and the target's owner (Visible); everyone else
/// in the workspace sees who, where and when. Hash is what an approval must send back; Interpreter asks for the red
/// notice; RecentOutput says the requester read another machine's output in the last minutes (a hint of injection).
/// </summary>
public sealed record WebRunView(Guid Id, Guid TargetAgentId, string TargetHostname, string TargetUser, Guid RequesterAgentId,
    string RequesterHostname, string RequesterUser, Guid? RequesterSessionId, bool Visible, string Mode, IReadOnlyList<string>? Argv,
    string? ShellCommand, string? Cwd, int TimeoutSeconds, string? Reason, string Status, int? ExitCode, string? Error,
    long OutputBytes, bool OutputTruncated, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? EndedAt,
    Guid? GrantId, Guid? JobId, bool CanDecide, bool SelfApproval, string? Hash, bool Interpreter, bool RecentOutput,
    string? ProjectKey = null, string? Kind = null, string? ScriptInterpreter = null, string? ScriptBody = null, IReadOnlyList<string>? ScriptArgs = null,
    string? InterpreterPath = null, string? TargetEnvironment = null, string? TargetFunction = null, bool Strict = false, bool NeedsCode = false,
    long? OutputCap = null, long? OutputReadBytes = null, long? OutputDroppedBytes = null, long? OutputRefusedBytes = null, int? OutputMissingFromSeq = null,
    int? OutputChunks = null, bool? OutputComplete = null, int? TestsPassed = null, int? TestsFailed = null, int? TestsSkipped = null,
    IReadOnlyList<ScriptFile>? ScriptFiles = null, string? ScriptStdin = null, bool ApprovalsRequired = false, string? ApprovalKeyId = null);

public static class RunQueries
{
    public static readonly TimeSpan RecentOutputWindow = TimeSpan.FromMinutes(10);

    public static async Task<RunView?> ForRequesterAsync(PodiumDb db, Guid id, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = await (from r in db.RemoteRuns.AsNoTracking()
                         join a in db.Agents.AsNoTracking() on r.TargetAgentId equals a.Id
                         join m in db.Machines.AsNoTracking() on a.MachineId equals m.Id
                         where r.Id == id && r.RequesterUserId == userId
                         select new { r, m.Hostname }).FirstOrDefaultAsync(ct);
        return row is null ? null : View(row.r, row.Hostname);
    }

    public static RunView View(RemoteRun r, string hostname)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new RunView(r.Id, r.TargetAgentId, hostname, r.Mode, r.Argv, r.ShellCommand, r.Cwd, r.TimeoutSeconds, r.Status,
            r.ExitCode, r.Error, r.OutputBytes, r.OutputTruncated, r.CreatedAt, r.ExpiresAt, r.EndedAt, r.GrantId, r.JobId,
            r.ProjectKey, r.Kind, r.TestsPassed, r.TestsFailed, r.TestsSkipped, r.ScriptInterpreter, r.ScriptBody, r.ScriptArgs, r.TargetEnvironment,
            r.TargetFunction, r.OutputCap, r.OutputReadBytes, r.OutputDroppedBytes, r.OutputRefusedBytes, r.OutputMissingFromSeq, r.OutputChunks, r.OutputComplete, ScriptFiles: r.ScriptFiles, ScriptStdin: r.ScriptStdin);
    }

    public static async Task<RunOutputPage> OutputAsync(PodiumDb db, Guid runId, int after, int limit, bool done, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var chunks = await db.RemoteRunOutput.AsNoTracking()
            .Where(o => o.RunId == runId && o.Seq > after).OrderBy(o => o.Seq).Take(limit)
            .Select(o => new RunOutputChunk(o.Seq, o.Stream, o.Body, o.GapBefore)).ToListAsync(ct);
        return new RunOutputPage(chunks, chunks.Count > 0 ? chunks[^1].Seq : after, done && chunks.Count < limit);
    }

    /// <summary>The home-relative path of the trusted tool that would run this script's interpreter, for the approval card.</summary>
    private static string? ScriptPath(string? interpretersJson, string? interpreter)
    {
        var tools = ScriptInterpreters.Tools(interpreter);
        return MachineFacts.Tools(interpretersJson, forView: false)?.FirstOrDefault(t => t.Trusted && tools.Contains(t.Name))?.Path;
    }

    /// <summary>True when the target is strict now (production or database, set or claimed).</summary>
    private static bool StrictNow(string? roleEnvironment, string? roleFunction, string? claimedEnvironment, string? claimedFunction) =>
        MachineRoles.IsStrict(MachineRoles.EffectiveEnvironment(roleEnvironment, claimedEnvironment), MachineRoles.EffectiveFunction(roleFunction, claimedFunction));

    /// <summary>Runs of a workspace for the web, newest first, key-paged; optionally one target agent's.</summary>
    public static async Task<List<WebRunView>> ForWebAsync(PodiumDb db, ApiConfig config, Guid workspaceId, Guid userId,
        Guid? targetAgentId, Guid? runId, DateTimeOffset? before, int limit, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await (from r in db.RemoteRuns.AsNoTracking()
                          join ta in db.Agents.AsNoTracking() on r.TargetAgentId equals ta.Id
                          join tm in db.Machines.AsNoTracking() on ta.MachineId equals tm.Id
                          join tu in db.Users.AsNoTracking() on r.TargetUserId equals tu.Id
                          join ra in db.Agents.AsNoTracking() on r.RequesterAgentId equals ra.Id
                          join rm in db.Machines.AsNoTracking() on ra.MachineId equals rm.Id
                          join ru in db.Users.AsNoTracking() on r.RequesterUserId equals ru.Id
                          where r.WorkspaceId == workspaceId
                                && (targetAgentId == null || r.TargetAgentId == targetAgentId)
                                && (runId == null || r.Id == runId)
                                && (before == null || r.CreatedAt < before)
                          orderby r.CreatedAt descending, r.Id descending
                          select new { r, Target = tm.Hostname, TargetOs = tm.Os, TargetUser = tu.DisplayName, Requester = rm.Hostname, RequesterUser = ru.DisplayName, ta.Interpreters, ta.RoleEnvironment, ta.RoleFunction, ta.ClaimedEnvironment, ta.ClaimedFunction, ta.ApprovalsRequired })
            .Take(limit).ToListAsync(ct);
        var requesters = rows.Where(x => x.r.Status == RunStatuses.PendingApproval).Select(x => x.r.RequesterAgentId).Distinct().ToList();
        var since = now - RecentOutputWindow;
        var readRecently = await db.RemoteRuns.AsNoTracking()
            .Where(r => requesters.Contains(r.RequesterAgentId) && r.EndedAt > since && r.OutputBytes > 0)
            .Select(r => r.RequesterAgentId).Distinct().ToListAsync(ct);
        return rows.Select(x =>
        {
            var r = x.r;
            var visible = r.RequesterUserId == userId || r.TargetUserId == userId;
            var pending = r.Status == RunStatuses.PendingApproval;
            return new WebRunView(r.Id, r.TargetAgentId, x.Target, x.TargetUser, r.RequesterAgentId, x.Requester, x.RequesterUser,
                r.RequesterSessionId, visible, r.Mode, visible ? r.Argv : null, visible ? r.ShellCommand : null, visible ? r.Cwd : null,
                r.TimeoutSeconds, visible ? r.Reason : null, r.Status, r.ExitCode, visible ? r.Error : null, r.OutputBytes,
                r.OutputTruncated, r.CreatedAt, r.ExpiresAt, r.EndedAt, r.GrantId, r.JobId,
                CanDecide: pending && r.TargetUserId == userId, SelfApproval: r.TargetUserId == r.RequesterUserId,
                Hash: pending && r.TargetUserId == userId ? RunCreator.RunHash(r) : null,
                Interpreter: r.Mode is RunModes.Shell or RunModes.Script || (r.Argv is { Count: > 0 } argv && GrantMatcher.IsInterpreter(argv[0], x.TargetOs)),
                RecentOutput: pending && readRecently.Contains(r.RequesterAgentId),
                ProjectKey: visible ? r.ProjectKey : null, Kind: r.Kind,
                ScriptInterpreter: r.ScriptInterpreter, ScriptBody: visible ? r.ScriptBody : null, ScriptArgs: visible ? r.ScriptArgs : null,
                InterpreterPath: r.Mode == RunModes.Script && visible ? ScriptPath(x.Interpreters, r.ScriptInterpreter) : null,
                TargetEnvironment: r.TargetEnvironment, TargetFunction: r.TargetFunction,
                Strict: r.Mode == RunModes.Script && StrictNow(x.RoleEnvironment, x.RoleFunction, x.ClaimedEnvironment, x.ClaimedFunction),
                NeedsCode: pending && RunCodeRule.NeedsCode(r.Mode, r.Argv, x.TargetOs, StrictNow(x.RoleEnvironment, x.RoleFunction, x.ClaimedEnvironment, x.ClaimedFunction)),
                OutputCap: r.OutputCap, OutputReadBytes: r.OutputReadBytes, OutputDroppedBytes: r.OutputDroppedBytes, OutputRefusedBytes: r.OutputRefusedBytes,
                OutputMissingFromSeq: r.OutputMissingFromSeq, OutputChunks: r.OutputChunks, OutputComplete: r.OutputComplete,
                TestsPassed: r.TestsPassed, TestsFailed: r.TestsFailed, TestsSkipped: r.TestsSkipped,
                ScriptFiles: visible ? r.ScriptFiles : null, ScriptStdin: visible ? r.ScriptStdin : null, ApprovalsRequired: x.ApprovalsRequired, ApprovalKeyId: r.ApprovalKeyId);
        }).ToList();
    }
}
