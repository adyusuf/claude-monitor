using System.Text.Json;
using Podium.Api.Approvals;
using Podium.Api.Background;
using Podium.Api.Remote;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Streaming;
using Podium.Api.Text;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Podium.Api.Endpoints;

public sealed record CommandRequest(string? Kind, string? Body);
public sealed record CommandRow(Guid Id, string Kind, string? Body, string Status, Guid CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? DeliveredAt, DateTimeOffset? AppliedAt, string? Result,
    long? ReplyEventId = null, string? ReplyText = null, bool ReplyMore = false, string? Transport = null, bool ContentRemoved = false);
/// <param name="ContentRemoved">The tool input was removed after the workspace's retention (F-119): <c>ToolInput</c> is then <c>{}</c>.</param>
public sealed record PermissionRow(Guid Id, string ToolName, System.Text.Json.JsonDocument ToolInput, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? Decision, string? Reason, DateTimeOffset? AnsweredAt, bool ContentRemoved = false);
/// <param name="Remember">How long the allow stands for the same tool on the same machine (<see cref="ApprovalDurations"/>; ADR-0019); null is once.</param>
/// <param name="Code">The two-step code an <c>always</c> rule may need (re-authentication).</param>
/// <param name="Other">The question tool's typed answers (question text to text; Q-2): <c>Answers</c> holds only offered options, this the "Other" text, which the agent wraps as a message from the web.</param>
public sealed record AnswerRequest(string? Decision, string? Reason, Dictionary<string, string>? Answers = null, string? Remember = null, string? Code = null,
    Dictionary<string, string>? Other = null);

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

    private static async Task<IResult> ListCommands(Guid id, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock)
    {
        if (await Access.SessionAsync(db, http.User.UserId(), id, http.RequestAborted) is not { } found) return Http.NotFound();
        var cutoff = await RetentionCutoffAsync(db, found.Session.WorkspaceId, clock, http.RequestAborted);
        // A prompt past retention reads as removed at once, also in the minute before the housekeeper blanks it (ContentRetention, F-119); a pending one never.
        var rows = (await db.SessionCommands.AsNoTracking().Where(c => c.SessionId == id).OrderByDescending(c => c.CreatedAt).Take(50)
            .Select(c => new CommandRow(c.Id, c.Kind, c.Body, c.Status, c.CreatedBy, c.CreatedAt, c.ExpiresAt, c.DeliveredAt, c.AppliedAt, c.Result, null, null, false, c.Transport, false))
            .ToListAsync(http.RequestAborted))
            .Select(c => c.Kind == CommandKinds.Prompt && c.Status is not (CommandStatuses.Queued or CommandStatuses.Delivered) && c.CreatedAt < cutoff
                ? c with { Body = null, ContentRemoved = true }
                : c).ToList();
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
        // A NUL or half a surrogate pair cannot be stored: refused here, not a 500 from the database.
        if (req.Kind == CommandKinds.Prompt && (string.IsNullOrWhiteSpace(req.Body) || req.Body.Length > BodyMax || !StorableText.Plain(req.Body)))
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
        var command = await db.SessionCommands.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, http.RequestAborted);
        if (command is null || await Access.SessionAsync(db, userId, command.SessionId, http.RequestAborted) is not { } found)
        {
            return Http.NotFound();
        }

        if (!await Access.OwnsSessionAsync(db, userId, found.Session, http.RequestAborted)) return Results.Forbid();
        // Compare-and-set: only a command still queued is cancelled, so the agent's "delivered" report and this cancel cannot both win.
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        var cancelled = await db.SessionCommands.Where(c => c.Id == id && c.Status == CommandStatuses.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CommandStatuses.Cancelled), http.RequestAborted);
        if (cancelled == 0) return Results.Conflict();
        Audit.Add(db, http, clock, AuditActions.CommandCancelled, command.WorkspaceId, userId, targetType: "command", targetId: id);
        await db.SaveChangesAsync(http.RequestAborted);
        await tx.CommitAsync(http.RequestAborted);
        broker.Publish(Broker.Workspace(command.WorkspaceId), new StreamMessage("command", new { sessionId = command.SessionId, id, status = CommandStatuses.Cancelled }));
        return Results.NoContent();
    }

    private static async Task<IResult> ListPermissions(Guid id, string? status, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        if (await Access.SessionAsync(db, http.User.UserId(), id, http.RequestAborted) is not { } found) return Http.NotFound();
        var cutoff = await RetentionCutoffAsync(db, found.Session.WorkspaceId, clock, http.RequestAborted);
        var query = db.PermissionRequests.AsNoTracking().Where(p => p.SessionId == id);
        if (status is { Length: > 0 }) query = query.Where(p => p.Status == status);
        var rows = await query.OrderByDescending(p => p.CreatedAt).Take(50)
            .Select(p => new PermissionRow(p.Id, p.ToolName, p.ToolInput, p.Status, p.CreatedAt, p.ExpiresAt, p.Decision, p.Reason, p.AnsweredAt, false))
            .ToListAsync(http.RequestAborted);
        // An input past retention reads as removed at once (ContentRetention, F-119); an open request never.
        return Results.Ok(rows.Select(p => p.Status != PermissionStatuses.Open && p.CreatedAt < cutoff
            ? p with { ToolInput = JsonDocument.Parse(ContentRetention.BlankInput), ContentRemoved = true }
            : p).ToList());
    }

    /// <summary>Content created before this moment is past the workspace's retention (<see cref="ContentRetention"/>).</summary>
    internal static async Task<DateTimeOffset> RetentionCutoffAsync(PodiumDb db, Guid workspaceId, TimeProvider clock, CancellationToken ct)
    {
        var days = await db.WorkspaceSettings.AsNoTracking().Where(s => s.WorkspaceId == workspaceId).Select(s => s.RetentionDays).FirstAsync(ct);
        return clock.GetUtcNow().AddDays(-days);
    }

    private static async Task<IResult> Answer(Guid id, AnswerRequest req, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock, Broker broker,
        ILoggerFactory logs)
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
        if (!StorableText.Plain(req.Reason)) return Http.Invalid("reason", "invalid_reason");
        var (answersError, answers) = QuestionAnswerCheck.Check(request, req.Decision!, req.Answers, req.Other);
        if (answersError is not null) return Http.Invalid("answers", answersError);
        var remember = req.Remember ?? ApprovalDurations.Once;
        if (!ApprovalDurations.All.Contains(remember)) return Http.Invalid("remember", "invalid_duration");
        if (remember != ApprovalDurations.Once && req.Decision != PermissionDecisions.Allow) return Http.Invalid("remember", RuleNotAllowed);
        var now = clock.GetUtcNow();
        if (request.Status != PermissionStatuses.Open || request.ExpiresAt <= now) return RemoteChecks.Conflict(RemoteErrors.NotPending);
        RuleAsk? ask = null;
        Guid? existing = null;
        if (remember != ApprovalDurations.Once)
        {
            var (refused, asked, standing) = await ApprovalRuleCreation.RememberAsync(db, http, config, clock, userId, request, remember, req.Code, now,
                logs.CreateLogger(typeof(ApprovalRuleCreation)));
            if (refused is not null) return refused;
            ask = asked;
            existing = standing;
        }

        var decision = req.Decision!;
        var reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim();
        var storedAnswers = answers is null ? null : JsonSerializer.Serialize(answers); // kept so the obsolete poll returns them too (F-196)
        Audit.Add(db, http, clock, AuditActions.PermissionAnswered, request.WorkspaceId, userId, targetType: "permission_request",
            targetId: id, detail: new { Decision = decision, request.ToolName });
        // The answer, a new rule or the change to an existing one, and their audit rows are saved together or not at all: an answer that is
        // not saved stays open (the owner can answer again), and one that is saved always reaches the agent below. The answer itself is a
        // compare-and-set on a request still open and not expired: of two answers at once (the band, the approvals page and the session page
        // show the same request), the database lets one through and the other saves nothing, sends nothing and gets 409.
        for (var attempt = 0; ; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
            try
            {
                var answered = await db.PermissionRequests
                    .Where(p => p.Id == id && p.Status == PermissionStatuses.Open && p.ExpiresAt > now)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PermissionStatuses.Answered).SetProperty(p => p.Decision, decision)
                        .SetProperty(p => p.Reason, reason).SetProperty(p => p.AnsweredBy, userId).SetProperty(p => p.AnsweredAt, now)
                        .SetProperty(p => p.Answers, storedAnswers), http.RequestAborted);
                if (answered != 1)
                {
                    await tx.RollbackAsync(http.RequestAborted);
                    return RemoteChecks.Conflict(RemoteErrors.NotPending); // answered meanwhile, or expired: the page says which in words
                }

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
            new PermissionAnswerMessage(id, found.Session.ExternalId, decision, reason, answers)));
        broker.Publish(Broker.Workspace(request.WorkspaceId), new StreamMessage("permission", new { sessionId = request.SessionId, id, status = PermissionStatuses.Answered }));
        return Results.NoContent();
    }

    public const string RuleNotAllowed = "rule_not_allowed";
    public const string RuleLimit = "rule_limit";

    /// <summary>A path tool's path is not inside the project's access (ADR-0020 §4): the request is answered once, never remembered.</summary>
    public const string RuleOutsideAccess = "rule_outside_access";
}
