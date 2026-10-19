using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Endpoints;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// The fixed actions a machine's owner asks of its agent from the web (ADR-0005, "Agent actions from the web"). The request is only
/// a kind from a closed set, plus (for the two Claude kinds of ADR-0014) a project key, a permission mode from a closed list or the short id of a session the
/// agent started; the agent performs it with its own code. Fail-closed: a member of the workspace, the machine's owner,
/// and for restart and update a recent sign-in (a fresh two-step code on a strict target). Every state change is a compare-and-set
/// and is published after it is committed.
/// </summary>
public static class AgentActions
{
    public const string WorkspaceEvent = "action";
    public const int ListMax = 10;

    public static async Task<IResult> CreateAsync(Guid agentId, AgentActionRequest req, HttpContext http, MonitorDb db, ApiConfig config,
        TimeProvider clock, Broker broker)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        var userId = http.User.UserId();
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId && a.Status == AgentStatuses.Active, http.RequestAborted);
        if (agent is null || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        if (agent.UserId != userId) return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: AgentActionErrors.OwnerOnly);
        if (!AgentActionKinds.All.Contains(req.Kind ?? "")) return Http.Invalid("kind", AgentActionErrors.InvalidKind);
        var kind = req.Kind!;
        var claude = AgentActionKinds.IsClaude(kind) ? await ClaudeCheckAsync(db, agent, kind, req, http.RequestAborted) : null;
        if (claude?.Refusal is { } claudeRefused) return claudeRefused;
        if (AgentActionKinds.NeedsReauth(kind)
            && await RemoteChecks.ApprovalGateAsync(http, db, config, clock, userId, agent, req.Code) is { } refused)
        {
            return refused;
        }

        var now = clock.GetUtcNow();
        await ExpireStaleAsync(db, agentId, now, http.RequestAborted);
        if (await OpenOfKindAsync(db, agentId, kind, http.RequestAborted) is { } open) return Results.Ok(View(open));

        var action = new AgentAction
        {
            WorkspaceId = agent.WorkspaceId,
            AgentId = agentId,
            RequestedBy = userId,
            Kind = kind,
            ProjectKey = claude?.ProjectKey,
            PermissionMode = claude?.PermissionMode,
            SessionRef = claude?.SessionRef,
            Prompt = claude?.Prompt,
            CreatedAt = now,
            ExpiresAt = now + AgentActionKinds.QueueTtl,
        };
        db.AgentActions.Add(action);
        Audit.Add(db, http, clock, AuditActions.AgentActionRequested, agent.WorkspaceId, userId, targetType: "agent", targetId: agentId,
            detail: claude is null ? new { kind } : (object)new { kind, projectKey = claude.ProjectKey, permissionMode = claude.PermissionMode, sessionRef = claude.SessionRef, hasPrompt = claude.Prompt is not null });
        try
        {
            await db.SaveChangesAsync(http.RequestAborted);
        }
        catch (DbUpdateException)
        {
            // Another request of the same kind won the one-open-action index: that one is the answer.
            db.ChangeTracker.Clear();
            return await OpenOfKindAsync(db, agentId, kind, http.RequestAborted) is { } won ? Results.Ok(View(won)) : throw new InvalidOperationException("agent action not stored");
        }

        Changed(broker, action);
        broker.Publish(Broker.Agent(agentId), new StreamMessage(AgentStreamEvents.Action, new AgentActionMessage(action.Id, action.Kind, action.ExpiresAt, action.ProjectKey, action.PermissionMode, action.SessionRef, action.Prompt)));
        return Results.Ok(View(action));
    }

    /// <summary>The owner's recent actions, newest first. Others get nothing: the result text can name this machine's folders.</summary>
    public static async Task<IResult> ListAsync(Guid agentId, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        var userId = http.User.UserId();
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId && a.Status == AgentStatuses.Active, http.RequestAborted);
        if (agent is null || agent.UserId != userId || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Viewer, http.RequestAborted) is null)
        {
            return Http.NotFound();
        }

        await ExpireStaleAsync(db, agentId, clock.GetUtcNow(), http.RequestAborted);
        var rows = await db.AgentActions.AsNoTracking().Where(a => a.AgentId == agentId)
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(ListMax).ToListAsync(http.RequestAborted);
        return Results.Ok(rows.Select(View).ToList());
    }

    /// <summary>The queued actions of an agent, for the replay when its stream opens. Delivered ones are not replayed: the agent already took them.</summary>
    public static async Task<List<AgentActionMessage>> WaitingForAsync(MonitorDb db, Guid agentId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        await ExpireStaleAsync(db, agentId, now, ct);
        return await db.AgentActions.AsNoTracking()
            .Where(a => a.AgentId == agentId && a.Status == AgentActionStatuses.Queued && a.ExpiresAt > now)
            .OrderBy(a => a.CreatedAt).Select(a => new AgentActionMessage(a.Id, a.Kind, a.ExpiresAt, a.ProjectKey, a.PermissionMode, a.SessionRef, a.Prompt)).ToListAsync(ct);
    }

    /// <summary>
    /// The agent's report. "delivered" takes a queued action (the one and only start: a replayed message finds it taken and gets a 409) and gives it a
    /// fresh <see cref="AgentActionKinds.QueueTtl"/> to finish in, so a slow one is not expired just because it waited to be taken,
    /// "succeeded" and "failed" finish a delivered one. A report from another agent is a 404; a final status never changes.
    /// </summary>
    public static async Task<IResult> ReportAsync(Guid id, AgentActionReport req, Guid agentId, HttpContext http, MonitorDb db, TimeProvider clock,
        Broker broker)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        if (!AgentActionStatuses.FromAgent.Contains(req.Status ?? "")) return Http.Invalid("status", "invalid_status");
        var action = await db.AgentActions.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.AgentId == agentId, http.RequestAborted);
        if (action is null) return Http.NotFound();

        var now = clock.GetUtcNow();
        var result = req.Result is { Length: > AgentActionKinds.ResultMax } r ? r[..AgentActionKinds.ResultMax] : req.Result;
        // A started Claude session tells the page which one it is, by the short id `claude --bg` printed; only a success of a start may name one.
        var sessionRef = action.Kind == AgentActionKinds.ClaudeStart && req.Status == AgentActionStatuses.Succeeded && AgentActionKinds.IsSessionRef(req.SessionRef) ? req.SessionRef : null;
        var changed = req.Status == AgentActionStatuses.Delivered
            ? await db.AgentActions.Where(a => a.Id == id && a.Status == AgentActionStatuses.Queued && a.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AgentActionStatuses.Delivered).SetProperty(a => a.DeliveredAt, now).SetProperty(a => a.ExpiresAt, now + AgentActionKinds.QueueTtl).SetProperty(a => a.Prompt, (string?)null), http.RequestAborted)
            : await db.AgentActions.Where(a => a.Id == id && a.Status == AgentActionStatuses.Delivered)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, req.Status!).SetProperty(a => a.Result, result).SetProperty(a => a.FinishedAt, now).SetProperty(a => a.SessionRef, a => sessionRef ?? a.SessionRef).SetProperty(a => a.Prompt, (string?)null), http.RequestAborted);
        if (changed != 1) return Results.Conflict();
        action.Status = req.Status!;
        Changed(broker, action);
        return Results.NoContent();
    }

    public static AgentActionView View(AgentAction a) =>
        new(a.Id, a.AgentId, a.Kind, a.Status, a.Result, a.CreatedAt, a.ExpiresAt, a.FinishedAt, a.ProjectKey, a.PermissionMode, a.SessionRef);

    /// <summary>What a Claude request was checked to be: its (cleaned) fields, or why it is refused.</summary>
    private sealed record ClaudeCheck(string? ProjectKey, string? PermissionMode, string? SessionRef, IResult? Refusal, string? Prompt = null);

    /// <summary>
    /// The rules of starting and stopping Claude from the web (ADR-0014), on top of the owner-only rule: the workspace's remote-work switch, the machine owner's own
    /// opt-in, an admin-set environment of dev or test that no claim makes stricter (production and database machines never), a Claude Code on the machine, no
    /// service agent, and fields that are keys and enums, never text. A stop names only a session this agent's earlier start returned.
    /// </summary>
    private static async Task<ClaudeCheck> ClaudeCheckAsync(MonitorDb db, Agent agent, string kind, AgentActionRequest req, CancellationToken ct)
    {
        static ClaudeCheck Refuse(int status, string title) => new(null, null, null, Results.Problem(statusCode: status, title: title));
        var settings = await db.WorkspaceSettings.AsNoTracking().FirstAsync(s => s.WorkspaceId == agent.WorkspaceId, ct);
        if (!settings.RemoteRunsEnabled) return Refuse(StatusCodes.Status409Conflict, RemoteErrors.Disabled);
        if (!agent.AcceptsClaudeControl) return Refuse(StatusCodes.Status409Conflict, AgentActionErrors.ClaudeNotAccepted);
        if (agent.ServiceMode) return Refuse(StatusCodes.Status409Conflict, AgentActionErrors.ClaudeServiceMode);
        if (agent.RoleEnvironment is null) return Refuse(StatusCodes.Status409Conflict, RemoteErrors.RoleNotSet);
        var environment = MachineRoles.EffectiveEnvironment(agent.RoleEnvironment, agent.ClaimedEnvironment);
        var function = MachineRoles.EffectiveFunction(agent.RoleFunction, agent.ClaimedFunction);
        if (!AgentActionKinds.ClaudeEnvironments.Contains(agent.RoleEnvironment) || MachineRoles.IsStrict(environment, function))
        {
            return Refuse(StatusCodes.Status409Conflict, AgentActionErrors.ClaudeEnvironment);
        }

        if (agent.ClaudeKind is null or ClaudeKinds.None) return Refuse(StatusCodes.Status409Conflict, AgentActionErrors.ClaudeNotInstalled);
        if (kind == AgentActionKinds.ClaudeStart)
        {
            if (req.ProjectKey is not { Length: >= 1 and <= RunKinds.ProjectKeyMax } project || project.Any(char.IsControl)) return new(null, null, null, Http.Invalid("projectKey", AgentActionErrors.InvalidProject));
            var mode = req.PermissionMode ?? AgentActionKinds.DefaultClaudePermissionMode;
            if (!AgentActionKinds.ClaudePermissionModes.Contains(mode)) return new(null, null, null, Http.Invalid("permissionMode", AgentActionErrors.InvalidPermissionMode));
            var prompt = req.Prompt?.Trim();
            if (prompt is { Length: > 0 } && (prompt.Length > AgentActionKinds.PromptMax || prompt.Any(c => char.IsControl(c) && c is not ('\n' or '\t'))))
            {
                return new(null, null, null, Http.Invalid("prompt", AgentActionErrors.InvalidPrompt));
            }

            return new(project, mode, null, null, string.IsNullOrEmpty(prompt) ? null : prompt);
        }

        if (req.Prompt is { Length: > 0 }) return new(null, null, null, Http.Invalid("prompt", AgentActionErrors.InvalidPrompt));
        if (!AgentActionKinds.IsSessionRef(req.SessionRef)) return new(null, null, null, Http.Invalid("sessionRef", AgentActionErrors.InvalidSessionRef));
        var started = await db.AgentActions.AsNoTracking().AnyAsync(a => a.AgentId == agent.Id && a.Kind == AgentActionKinds.ClaudeStart
            && a.Status == AgentActionStatuses.Succeeded && a.SessionRef == req.SessionRef, ct);
        return started ? new(null, null, req.SessionRef, null) : Refuse(StatusCodes.Status409Conflict, AgentActionErrors.NotStartedHere);
    }

    /// <summary>
    /// The machine owner's switch (ADR-0014): only the owner, a recent sign-in or a two-step code to turn it ON, an audit row either way. Turning it off ends every
    /// open Claude action of the machine at once, so nothing queued is started later.
    /// </summary>
    public static async Task<IResult> SetClaudeControlAsync(Guid agentId, AgentClaudeRequest req, HttpContext http, MonitorDb db, ApiConfig config, TimeProvider clock, Broker broker)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        var userId = http.User.UserId();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId && a.Status == AgentStatuses.Active, http.RequestAborted);
        if (agent is null || agent.UserId != userId || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        if (req.Accept && !agent.AcceptsClaudeControl && !await RemoteChecks.ReauthAsync(http, db, config, clock, userId, req.Code)) return RemoteChecks.ReauthRefused();
        var was = agent.AcceptsClaudeControl;
        var now = clock.GetUtcNow();
        agent.AcceptsClaudeControl = req.Accept;
        agent.AcceptsClaudeControlSetAt = now;
        agent.AcceptsClaudeControlSetBy = userId;
        Audit.Add(db, http, clock, AuditActions.AgentClaudeControlChanged, agent.WorkspaceId, userId, targetType: "agent", targetId: agentId, detail: new { before = was, after = req.Accept });
        await db.SaveChangesAsync(http.RequestAborted);
        if (was && !req.Accept)
        {
            await db.AgentActions.Where(a => a.AgentId == agentId && (a.Kind == AgentActionKinds.ClaudeStart || a.Kind == AgentActionKinds.ClaudeStop)
                    && (a.Status == AgentActionStatuses.Queued || a.Status == AgentActionStatuses.Delivered))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AgentActionStatuses.Expired).SetProperty(a => a.FinishedAt, now).SetProperty(a => a.Prompt, (string?)null), http.RequestAborted);
        }

        broker.Publish(Broker.Workspace(agent.WorkspaceId), new StreamMessage(AgentRoleEndpoints.MachineEvent, new { agentId }));
        return Results.Ok(new AgentClaudeView(agentId, agent.AcceptsClaudeControl, agent.AcceptsClaudeControlSetAt, agent.AcceptsClaudeControlSetBy));
    }

    private static Task<AgentAction?> OpenOfKindAsync(MonitorDb db, Guid agentId, string kind, CancellationToken ct) =>
        db.AgentActions.AsNoTracking().FirstOrDefaultAsync(a => a.AgentId == agentId && a.Kind == kind
            && (a.Status == AgentActionStatuses.Queued || a.Status == AgentActionStatuses.Delivered), ct);

    /// <summary>An open action past its time is expired, so it no longer blocks a new one and is never delivered late.</summary>
    private static Task<int> ExpireStaleAsync(MonitorDb db, Guid agentId, DateTimeOffset now, CancellationToken ct) =>
        db.AgentActions.Where(a => a.AgentId == agentId && (a.Status == AgentActionStatuses.Queued || a.Status == AgentActionStatuses.Delivered)
                && a.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AgentActionStatuses.Expired).SetProperty(a => a.FinishedAt, now).SetProperty(a => a.Prompt, (string?)null), ct);

    private static void Changed(Broker broker, AgentAction a) =>
        broker.Publish(Broker.Workspace(a.WorkspaceId), new StreamMessage(WorkspaceEvent, new { id = a.Id, agentId = a.AgentId, status = a.Status }));
}
