using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ClaudeMonitor.Api.Remote;

/// <summary>The outcome of an ask request: the ask, or why it was refused (an HTTP status and an error code).</summary>
public sealed record AskCreateResult(AskCreated? Ask, int Status, string? Error)
{
    public static AskCreateResult Refused(int status, string error) => new(null, status, error);
}

/// <summary>
/// A Claude session asks a question of another machine of the same user (ADR-0008 §3f). Fail-closed at every step: the workspace switch, both
/// memberships, the same user and workspace, the target's opt-in, its set and non-strict role and its `ask` capability, the depth of one, no loop
/// and the rate caps; the caps are counted under the row locks of both agents (taken in id order) so two crossing asks cannot both pass. The
/// question is text from a model: it is stored for the target and never logged; the audit row names the ask by id.
/// </summary>
public sealed class AskCreator(MonitorDb db, ApiConfig config, TimeProvider clock, Broker broker)
{
    private const string UniqueViolation = "23505";
    public const int LabelMax = 64;

    public async Task<AskCreateResult> CreateAsync(AskCreate req, Guid agentId, Guid userId, Guid workspaceId, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(req);
        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        if (req.ClientKey is not { Length: >= 1 and <= RunRules.ClientKeyMax } || req.ClientKey.Any(char.IsControl)) return Refused(400, RunRules.BadClientKey);
        if (req.Question is not { Length: >= 1 } question || question.Length > config.AskQuestionMax || question.Contains('\0') || string.IsNullOrWhiteSpace(question))
        {
            return Refused(400, AskErrors.InvalidQuestion);
        }

        if (req.ProjectKey is { } project && (project.Length is < 1 or > RunKinds.ProjectKeyMax || project.Any(char.IsControl))) return Refused(400, RunRules.BadProject);
        var settings = await db.WorkspaceSettings.AsNoTracking().FirstAsync(s => s.WorkspaceId == workspaceId, ct);
        if (!settings.RemoteRunsEnabled) return Refused(409, RemoteErrors.Disabled);
        if (await Access.MemberAsync(db, userId, workspaceId, Roles.Member, ct) is null) return Refused(404, "not_found");

        var requester = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId && a.Status == AgentStatuses.Active, ct);
        var target = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == req.TargetAgentId && a.Status == AgentStatuses.Active, ct);
        if (requester is null || target is null) return Refused(404, "not_found");
        if (target.Id == requester.Id) return Refused(400, AskErrors.Mismatch);
        if (target.WorkspaceId != workspaceId || requester.WorkspaceId != workspaceId) return Refused(409, AskErrors.OtherWorkspace);
        if (target.UserId != userId || requester.UserId != userId) return Refused(409, AskErrors.OtherUser);

        var existing = await db.MachineAsks.AsNoTracking().FirstOrDefaultAsync(a => a.RequesterAgentId == agentId && a.ClientKey == req.ClientKey, ct);
        if (existing is not null) return Again(req, existing);

        if (Refusal(target) is { } refusal) return refusal;
        if (await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == target.UserId && m.RemovedAt == null, ct) is false)
        {
            return Refused(409, RemoteErrors.TargetCannotRun);
        }

        var session = req.SessionExternalId is null
            ? null
            : await db.HarnessSessions.AsNoTracking().Where(s => s.AgentId == agentId && s.ExternalId == req.SessionExternalId).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        var ask = new MachineAsk
        {
            WorkspaceId = workspaceId,
            RequesterAgentId = agentId,
            RequesterUserId = userId,
            RequesterSessionId = session,
            TargetAgentId = target.Id,
            TargetUserId = target.UserId,
            ClientKey = req.ClientKey,
            ProjectKey = req.ProjectKey,
            Question = question,
            CreatedAt = now,
            ExpiresAt = now + config.AskQueuedLifetime,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await LockAsync(workspaceId, userId, agentId, target.Id, ct) is { } locked) return locked;
        if (await LimitsAsync(agentId, target.Id, session, now, ct) is { } limited) return limited;
        db.MachineAsks.Add(ask);
        Audit.Add(db, http, clock, AuditActions.AskCreated, workspaceId, userId, agentId, targetType: "ask", targetId: ask.Id, detail: new { target = target.Id });
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return Again(req, await db.MachineAsks.AsNoTracking().FirstAsync(a => a.RequesterAgentId == agentId && a.ClientKey == req.ClientKey, ct));
        }

        var from = await (from a in db.Agents.AsNoTracking() join m in db.Machines.AsNoTracking() on a.MachineId equals m.Id where a.Id == agentId select m.Hostname).FirstAsync(ct);
        broker.Publish(Broker.Agent(target.Id), new StreamMessage(AgentStreamEvents.Ask, new AskMessage(ask.Id, question, Label(from), ask.ProjectKey, ask.ExpiresAt)));
        return new AskCreateResult(new AskCreated(ask.Id, ask.Status, ask.ExpiresAt), StatusCodes.Status200OK, null);
    }

    /// <summary>
    /// Why a machine takes no ask (ADR-0008 §3f): its owner did not opt in, no admin has set its environment, its effective role is strict, or it does
    /// not report the `ask` capability (no Claude Code there). Null when it takes asks.
    /// </summary>
    public static AskCreateResult? Refusal(Agent target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.AcceptsAsks) return Refused(409, AskErrors.NotAccepted);
        if (target.RoleEnvironment is null) return Refused(409, RemoteErrors.RoleNotSet);
        if (MachineRoles.IsStrict(MachineRoles.EffectiveEnvironment(target.RoleEnvironment, target.ClaimedEnvironment),
                MachineRoles.EffectiveFunction(target.RoleFunction, target.ClaimedFunction)))
        {
            return Refused(409, AskErrors.NotAccepted);
        }

        if (target.Capabilities?.Contains(AgentCapabilities.Ask) != true) return Refused(409, target.ClaudeKind == ClaudeKinds.None ? AskErrors.NoClaude : AskErrors.NotAccepted);
        return null;
    }

    /// <summary>A machine's label as another machine sees it: letters, digits and . _ - @ only, at most 64 characters.</summary>
    public static string Label(string? hostname)
    {
        var kept = new string((hostname ?? "").Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@' ? c : '?').ToArray());
        kept = kept.Length > LabelMax ? kept[..LabelMax] : kept;
        return kept.Length == 0 ? "?" : kept;
    }

    private static AskCreateResult Refused(int status, string error) => AskCreateResult.Refused(status, error);

    private static AskCreateResult Again(AskCreate req, MachineAsk ask) =>
        ask.TargetAgentId == req.TargetAgentId && ask.Question == req.Question
            ? new AskCreateResult(new AskCreated(ask.Id, ask.Status, ask.ExpiresAt), StatusCodes.Status200OK, null)
            : Refused(409, AskErrors.Mismatch);

    /// <summary>The workspace switch, the user's member row and both agent rows (in id order), locked until commit; a revoke, move, removal or switch-off waits and then cancels the ask.</summary>
    private async Task<AskCreateResult?> LockAsync(Guid workspaceId, Guid userId, Guid requesterId, Guid targetId, CancellationToken ct)
    {
        if (await db.WorkspaceSettings.Where(s => s.WorkspaceId == workspaceId && s.RemoteRunsEnabled)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RemoteRunsEnabled, x => x.RemoteRunsEnabled), ct) != 1)
        {
            return Refused(409, RemoteErrors.Disabled);
        }

        if (await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspaceId && m.UserId == userId && m.RemovedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, m => m.Role), ct) != 1)
        {
            return Refused(409, RemoteErrors.TargetCannotRun);
        }

        foreach (var id in new[] { requesterId, targetId }.Order())
        {
            if (await db.Agents.Where(a => a.Id == id && a.WorkspaceId == workspaceId && a.Status == AgentStatuses.Active && a.UserId == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.ExecLevel, a => a.ExecLevel), ct) != 1)
            {
                return Refused(409, RemoteErrors.TargetCannotRun);
            }
        }

        // The target is read again under its lock: an opt-in switched off a moment ago must not let this ask through.
        var target = await db.Agents.AsNoTracking().FirstAsync(a => a.Id == targetId, ct);
        return Refusal(target);
    }

    /// <summary>Depth one, no loop and the rate caps, counted while both agents are locked.</summary>
    private async Task<AskCreateResult?> LimitsAsync(Guid requesterId, Guid targetId, Guid? session, DateTimeOffset now, CancellationToken ct)
    {
        var depthSince = now - config.AskDepthWindow;
        if (await db.MachineAsks.AnyAsync(a => a.TargetAgentId == requesterId && a.DeliveredAt > depthSince, ct)) return Refused(409, AskErrors.Depth);
        if (await db.MachineAsks.AnyAsync(a => a.RequesterAgentId == targetId && a.TargetAgentId == requesterId
                                               && ((a.Status == AskStatuses.Queued || a.Status == AskStatuses.Delivered) || a.EndedAt > depthSince), ct))
        {
            return Refused(409, AskErrors.Loop);
        }

        if (await db.MachineAsks.CountAsync(a => a.RequesterAgentId == requesterId && a.CreatedAt > now.AddHours(-1), ct) >= config.AskPerAgentPerHour) return Refused(429, AskErrors.Rate);
        if (session is { } sessionId
            && await db.MachineAsks.CountAsync(a => a.RequesterSessionId == sessionId && a.CreatedAt > now.AddMinutes(-10), ct) >= config.AskPerSessionPer10Min)
        {
            return Refused(429, AskErrors.Rate);
        }

        return null;
    }
}
