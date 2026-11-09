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
        RuleAsk? ask = null;
        Guid? existing = null;
        if (remember != ApprovalDurations.Once)
        {
            var (refused, asked, standing) = await ApprovalRuleCreation.RememberAsync(db, http, config, clock, userId, request, remember, req.Code, now);
            if (refused is not null) return refused;
            ask = asked;
            existing = standing;
        }

        request.Status = PermissionStatuses.Answered;
        request.Decision = req.Decision;
        request.Reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim();
        request.AnsweredBy = userId;
        request.AnsweredAt = now;
        Audit.Add(db, http, clock, AuditActions.PermissionAnswered, request.WorkspaceId, userId, targetType: "permission_request",
            targetId: id, detail: new { request.Decision, request.ToolName });
        // The answer, a new rule or the change to an existing one, and their audit rows are saved together or not at all: an answer that is
        // not saved stays open (the owner can answer again), and one that is saved always reaches the agent below.
        for (var attempt = 0; ; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
            try
            {
                if (ask is not null && existing is { } ruleId && await ApprovalRuleCreation.StandLongerAsync(db, ruleId, ask, http.RequestAborted))
                {
                    ApprovalRuleCreation.AuditRule(db, http, clock, ask, ruleId);
                }

                await db.SaveChangesAsync(http.RequestAborted);
                await tx.CommitAsync(http.RequestAborted);
                break;
            }
            catch (DbUpdateException e) when (attempt == 0 && ask is not null
                && e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Another answer made the same standing approval a moment ago (one per scope, ux_approval_rules_scope): this answer stands, and
                // its duration is applied to that rule (extending it when this one lasts longer).
                await tx.RollbackAsync(http.RequestAborted);
                foreach (var added in db.ChangeTracker.Entries().Where(x => x.State == EntityState.Added
                    && (x.Entity is ApprovalRule || x.Entity is AuditEvent { Action: AuditActions.ApprovalRuleCreated })).ToList())
                {
                    added.State = EntityState.Detached;
                }

                var (again, standing) = await ApprovalRuleCreation.ApplyRuleAsync(db, http, clock, ask);
                if (again is not null) return again; // the rule cannot stand after all (the cap): nothing is saved, as on the first refusal
                existing = standing;
            }
        }

        broker.Publish(Broker.Agent(request.AgentId), new StreamMessage(AgentStreamEvents.PermissionAnswer,
            new PermissionAnswerMessage(id, found.Session.ExternalId, request.Decision!, request.Reason, req.Answers)));
        broker.Publish(Broker.Workspace(request.WorkspaceId), new StreamMessage("permission", new { sessionId = request.SessionId, id, status = request.Status }));
        return Results.NoContent();
    }

    public const string RuleNotAllowed = "rule_not_allowed";
    public const string RuleLimit = "rule_limit";

    /// <summary>A path tool's path is not inside the project's access (ADR-0020 §4): the request is answered once, never remembered.</summary>
    public const string RuleOutsideAccess = "rule_outside_access";

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
