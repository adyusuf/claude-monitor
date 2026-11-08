using System.Text.Json;
using Podium.Api.Approvals;
using Podium.Api.Remote;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Streaming;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Podium.Api.Endpoints;

public sealed record CommandRequest(string? Kind, string? Body);
public sealed record CommandRow(Guid Id, string Kind, string? Body, string Status, Guid CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? DeliveredAt, DateTimeOffset? AppliedAt, string? Result,
    long? ReplyEventId = null, string? ReplyText = null, bool ReplyMore = false, string? Transport = null);
public sealed record PermissionRow(Guid Id, string ToolName, System.Text.Json.JsonDocument ToolInput, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? Decision, string? Reason, DateTimeOffset? AnsweredAt);
/// <param name="Remember">How long the allow stands for the same tool on the same machine (<see cref="ApprovalDurations"/>; ADR-0019); null is once.</param>
/// <param name="Code">The two-step code an <c>always</c> rule may need (re-authentication).</param>
public sealed record AnswerRequest(string? Decision, string? Reason, Dictionary<string, string>? Answers = null, string? Remember = null, string? Code = null);

/// <summary>
/// Commands to a session and answers to its permission requests. Anyone in the workspace may READ them; only the
/// session's owner may create, cancel or answer (ADR-0002). Every change is audited.
/// </summary>
public static class CommandEndpoints
{
    public const int BodyMax = 10_000;
    public const int ReasonMax = 500;
    public const int AnswerMax = 500;
    public const int QuestionsMax = 4;

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/sessions/{id:guid}/commands", ListCommands);
        g.MapPost("/sessions/{id:guid}/commands", Create);
        g.MapPost("/commands/{id:guid}/cancel", Cancel);
        g.MapGet("/sessions/{id:guid}/permission-requests", ListPermissions);
        g.MapPost("/permission-requests/{id:guid}/answer", Answer);
    }

    private static async Task<IResult> ListCommands(Guid id, HttpContext http, PodiumDb db, ApiConfig config)
    {
        if (await Access.SessionAsync(db, http.User.UserId(), id, http.RequestAborted) is null) return Http.NotFound();
        var rows = await db.SessionCommands.AsNoTracking().Where(c => c.SessionId == id).OrderByDescending(c => c.CreatedAt).Take(50)
            .Select(c => new CommandRow(c.Id, c.Kind, c.Body, c.Status, c.CreatedBy, c.CreatedAt, c.ExpiresAt, c.DeliveredAt, c.AppliedAt, c.Result, null, null, false, c.Transport))
            .ToListAsync(http.RequestAborted);
        var replies = await CommandReplies.FindAsync(db, id, rows, config, http.RequestAborted);
        return Results.Ok(rows.Select(c => replies.TryGetValue(c.Id, out var r) ? c with { ReplyEventId = r.EventId, ReplyText = r.Text, ReplyMore = r.More } : c));
    }

    private static async Task<IResult> Create(Guid id, CommandRequest req, HttpContext http, PodiumDb db, ApiConfig config,
        TimeProvider clock, Broker broker)
    {
        var userId = http.User.UserId();
        if (await Access.SessionAsync(db, userId, id, http.RequestAborted) is not { } found) return Http.NotFound();
        if (!await Access.OwnsSessionAsync(db, userId, found.Session, http.RequestAborted)) return Results.Forbid();
        if (!CommandKinds.All.Contains(req.Kind ?? "")) return Http.Invalid("kind", "invalid_kind");
        if (req.Kind == CommandKinds.Prompt && (string.IsNullOrWhiteSpace(req.Body) || req.Body.Length > BodyMax))
        {
            return Http.Invalid("body", "invalid_body");
        }

        if (found.Session.Status == SessionStatuses.Ended) return Http.Invalid("session", "session_ended");
        var now = clock.GetUtcNow();
        var command = new SessionCommand
        {
            WorkspaceId = found.Session.WorkspaceId,
            SessionId = id,
            AgentId = found.Session.AgentId,
            Kind = req.Kind!,
            Body = req.Kind == CommandKinds.Prompt ? req.Body : null,
            CreatedBy = userId,
            CreatedAt = now,
            ExpiresAt = now + config.CommandLifetime,
            Status = CommandStatuses.Queued,
        };
        db.SessionCommands.Add(command);
        Audit.Add(db, http, clock, AuditActions.CommandCreated, command.WorkspaceId, userId, targetType: "session", targetId: id,
            detail: new { command.Id, command.Kind });
        await db.SaveChangesAsync(http.RequestAborted);
        broker.Publish(Broker.Agent(command.AgentId), new StreamMessage(AgentStreamEvents.Command,
            new AgentCommandMessage(command.Id, id, found.Session.ExternalId, command.Kind, command.Body, command.ExpiresAt)));
        broker.Publish(Broker.Workspace(command.WorkspaceId), new StreamMessage("command", new { sessionId = id, id = command.Id, status = command.Status }));
        return Results.Created($"/api/commands/{command.Id}", new { command.Id });
    }

    private static async Task<IResult> Cancel(Guid id, HttpContext http, PodiumDb db, TimeProvider clock, Broker broker)
    {
        var userId = http.User.UserId();
        var command = await db.SessionCommands.FirstOrDefaultAsync(c => c.Id == id, http.RequestAborted);
        if (command is null || await Access.SessionAsync(db, userId, command.SessionId, http.RequestAborted) is not { } found)
        {
            return Http.NotFound();
        }

        if (!await Access.OwnsSessionAsync(db, userId, found.Session, http.RequestAborted)) return Results.Forbid();
        if (command.Status != CommandStatuses.Queued) return Results.Conflict();
        command.Status = CommandStatuses.Cancelled;
        Audit.Add(db, http, clock, AuditActions.CommandCancelled, command.WorkspaceId, userId, targetType: "command", targetId: id);
        await db.SaveChangesAsync(http.RequestAborted);
        broker.Publish(Broker.Workspace(command.WorkspaceId), new StreamMessage("command", new { sessionId = command.SessionId, id, status = command.Status }));
        return Results.NoContent();
    }

    private static async Task<IResult> ListPermissions(Guid id, string? status, HttpContext http, PodiumDb db)
    {
        if (await Access.SessionAsync(db, http.User.UserId(), id, http.RequestAborted) is null) return Http.NotFound();
        var query = db.PermissionRequests.AsNoTracking().Where(p => p.SessionId == id);
        if (status is { Length: > 0 }) query = query.Where(p => p.Status == status);
        var rows = await query.OrderByDescending(p => p.CreatedAt).Take(50)
            .Select(p => new PermissionRow(p.Id, p.ToolName, p.ToolInput, p.Status, p.CreatedAt, p.ExpiresAt, p.Decision, p.Reason, p.AnsweredAt))
            .ToListAsync(http.RequestAborted);
        return Results.Ok(rows);
    }

    private static async Task<IResult> Answer(Guid id, AnswerRequest req, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock, Broker broker)
    {
        var userId = http.User.UserId();
        var request = await db.PermissionRequests.FirstOrDefaultAsync(p => p.Id == id, http.RequestAborted);
        if (request is null || await Access.SessionAsync(db, userId, request.SessionId, http.RequestAborted) is not { } found)
        {
            return Http.NotFound();
        }

        if (!await Access.OwnsSessionAsync(db, userId, found.Session, http.RequestAborted)) return Results.Forbid();
        if (!PermissionDecisions.All.Contains(req.Decision ?? "")) return Http.Invalid("decision", "invalid_decision");
        if (req.Reason is { Length: > ReasonMax }) return Http.Invalid("reason", "too_long");
        if (req.Answers is not null && !AnswersFit(request, req.Decision!, req.Answers)) return Http.Invalid("answers", "invalid_answers");
        var remember = req.Remember ?? ApprovalDurations.Once;
        if (!ApprovalDurations.All.Contains(remember)) return Http.Invalid("remember", "invalid_duration");
        if (remember != ApprovalDurations.Once && req.Decision != PermissionDecisions.Allow) return Http.Invalid("remember", RuleNotAllowed);
        var now = clock.GetUtcNow();
        if (request.Status != PermissionStatuses.Open || request.ExpiresAt <= now) return Results.Conflict();
        if (remember != ApprovalDurations.Once
            && await RememberAsync(db, http, config, clock, userId, request, remember, req.Code, now) is { } refused)
        {
            return refused;
        }

        request.Status = PermissionStatuses.Answered;
        request.Decision = req.Decision;
        request.Reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim();
        request.AnsweredBy = userId;
        request.AnsweredAt = now;
        Audit.Add(db, http, clock, AuditActions.PermissionAnswered, request.WorkspaceId, userId, targetType: "permission_request",
            targetId: id, detail: new { request.Decision, request.ToolName });
        try
        {
            await db.SaveChangesAsync(http.RequestAborted);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another answer made the same standing approval a moment ago (one per scope, ux_approval_rules_scope): this answer stands, the rule is theirs.
            foreach (var added in db.ChangeTracker.Entries().Where(x => x.State == EntityState.Added
                && (x.Entity is ApprovalRule || x.Entity is AuditEvent { Action: AuditActions.ApprovalRuleCreated })).ToList())
            {
                added.State = EntityState.Detached;
            }

            await db.SaveChangesAsync(http.RequestAborted);
        }

        broker.Publish(Broker.Agent(request.AgentId), new StreamMessage(AgentStreamEvents.PermissionAnswer,
            new PermissionAnswerMessage(id, found.Session.ExternalId, request.Decision!, request.Reason, req.Answers)));
        broker.Publish(Broker.Workspace(request.WorkspaceId), new StreamMessage("permission", new { sessionId = request.SessionId, id, status = request.Status }));
        return Results.NoContent();
    }

    public const string RuleNotAllowed = "rule_not_allowed";
    public const string RuleLimit = "rule_limit";

    /// <summary>
    /// Makes (or extends) the approval rule an allow asks for (ADR-0019): never on a strict machine or for a tool that cannot have one; an
    /// <c>always</c> rule needs re-authentication. The rule's scope is the request's own. Null when the rule is in place (saved with the answer).
    /// </summary>
    private static async Task<IResult?> RememberAsync(PodiumDb db, HttpContext http, ApiConfig config, TimeProvider clock, Guid userId,
        PermissionRequest request, string remember, string? code, DateTimeOffset now)
    {
        var ct = http.RequestAborted;
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.AgentId, ct);
        if (agent is null || agent.UserId != userId || agent.Status != AgentStatuses.Active || agent.WorkspaceId != request.WorkspaceId || ApprovalRules.Strict(agent)
            || ApprovalRules.ScopeOf(request.ToolName, request.ToolInput.RootElement) is not { } scope)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: RuleNotAllowed);
        }

        if (remember == ApprovalDurations.Always && !await RemoteChecks.ReauthAsync(http, db, config, clock, userId, code)) return RemoteChecks.ReauthRefused();
        DateTimeOffset? ends = ApprovalDurations.Length(remember) is { } length ? now + length : null;
        var repoId = await db.HarnessSessions.AsNoTracking().Where(s => s.Id == request.SessionId).Select(s => s.RepoId).FirstOrDefaultAsync(ct);
        var rule = await db.ApprovalRules.Where(r => r.AgentId == agent.Id && r.UserId == userId && r.WorkspaceId == agent.WorkspaceId && r.RepoId == repoId
                && r.ToolName == scope.Tool && r.Command == scope.Command && r.RevokedAt == null)
            .OrderBy(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        if (rule is null)
        {
            var live = await db.ApprovalRules.CountAsync(r => r.UserId == userId && r.RevokedAt == null && (r.ExpiresAt == null || r.ExpiresAt > now), ct);
            if (live >= ApprovalRuleLimits.LiveMax) return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: RuleLimit);
            rule = new ApprovalRule
            {
                WorkspaceId = agent.WorkspaceId, UserId = userId, AgentId = agent.Id, RepoId = repoId, ToolName = scope.Tool, Command = scope.Command,
                CreatedAt = now, CreatedFromRequest = request.Id,
            };
            db.ApprovalRules.Add(rule);
        }
        else if (rule.ExpiresAt is null && ends is not null)
        {
            return null; // an always rule is already in place: a shorter one would only narrow it
        }

        rule.Duration = remember;
        rule.ExpiresAt = ends;
        Audit.Add(db, http, clock, AuditActions.ApprovalRuleCreated, agent.WorkspaceId, userId, targetType: "approval_rule", targetId: rule.Id,
            detail: new { request = request.Id, rule.ToolName, rule.Duration });
        return null;
    }

    /// <summary>
    /// Answers are chosen options, so they are accepted only for the question tool, only with an allow, only for the
    /// questions the request really carries, and each as short text: the web can never put other input into a tool call.
    /// </summary>
    private static bool AnswersFit(PermissionRequest request, string decision, Dictionary<string, string> answers)
    {
        if (request.ToolName != QuestionTools.AskUserQuestion || decision != PermissionDecisions.Allow) return false;
        if (answers.Count is 0 or > QuestionsMax) return false;
        var asked = new HashSet<string>();
        if (request.ToolInput.RootElement.ValueKind == JsonValueKind.Object && request.ToolInput.RootElement.TryGetProperty("questions", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in list.EnumerateArray())
            {
                if (q.ValueKind == JsonValueKind.Object && q.TryGetProperty("question", out var text) && text.ValueKind == JsonValueKind.String) asked.Add(text.GetString()!);
            }
        }

        return answers.All(a => asked.Contains(a.Key) && !string.IsNullOrWhiteSpace(a.Value) && a.Value.Length <= AnswerMax);
    }
}
