using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

public sealed record CommandRequest(string? Kind, string? Body);
public sealed record CommandRow(Guid Id, string Kind, string? Body, string Status, Guid CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? DeliveredAt, DateTimeOffset? AppliedAt, string? Result);
public sealed record PermissionRow(Guid Id, string ToolName, System.Text.Json.JsonDocument ToolInput, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? Decision, string? Reason, DateTimeOffset? AnsweredAt);
public sealed record AnswerRequest(string? Decision, string? Reason);

/// <summary>
/// Commands to a session and answers to its permission requests. Anyone in the workspace may READ them; only the
/// session's owner may create, cancel or answer (ADR-0002). Every change is audited.
/// </summary>
public static class CommandEndpoints
{
    public const int BodyMax = 10_000;
    public const int ReasonMax = 500;

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/sessions/{id:guid}/commands", ListCommands);
        g.MapPost("/sessions/{id:guid}/commands", Create);
        g.MapPost("/commands/{id:guid}/cancel", Cancel);
        g.MapGet("/sessions/{id:guid}/permission-requests", ListPermissions);
        g.MapPost("/permission-requests/{id:guid}/answer", Answer);
    }

    private static async Task<IResult> ListCommands(Guid id, HttpContext http, MonitorDb db)
    {
        if (await Access.SessionAsync(db, http.User.UserId(), id, http.RequestAborted) is null) return Http.NotFound();
        var rows = await db.SessionCommands.AsNoTracking().Where(c => c.SessionId == id).OrderByDescending(c => c.CreatedAt).Take(50)
            .Select(c => new CommandRow(c.Id, c.Kind, c.Body, c.Status, c.CreatedBy, c.CreatedAt, c.ExpiresAt, c.DeliveredAt, c.AppliedAt, c.Result))
            .ToListAsync(http.RequestAborted);
        return Results.Ok(rows);
    }

    private static async Task<IResult> Create(Guid id, CommandRequest req, HttpContext http, MonitorDb db, ApiConfig config,
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

    private static async Task<IResult> Cancel(Guid id, HttpContext http, MonitorDb db, TimeProvider clock, Broker broker)
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

    private static async Task<IResult> ListPermissions(Guid id, string? status, HttpContext http, MonitorDb db)
    {
        if (await Access.SessionAsync(db, http.User.UserId(), id, http.RequestAborted) is null) return Http.NotFound();
        var query = db.PermissionRequests.AsNoTracking().Where(p => p.SessionId == id);
        if (status is { Length: > 0 }) query = query.Where(p => p.Status == status);
        var rows = await query.OrderByDescending(p => p.CreatedAt).Take(50)
            .Select(p => new PermissionRow(p.Id, p.ToolName, p.ToolInput, p.Status, p.CreatedAt, p.ExpiresAt, p.Decision, p.Reason, p.AnsweredAt))
            .ToListAsync(http.RequestAborted);
        return Results.Ok(rows);
    }

    private static async Task<IResult> Answer(Guid id, AnswerRequest req, HttpContext http, MonitorDb db, TimeProvider clock, Broker broker)
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
        var now = clock.GetUtcNow();
        if (request.Status != PermissionStatuses.Open || request.ExpiresAt <= now) return Results.Conflict();

        request.Status = PermissionStatuses.Answered;
        request.Decision = req.Decision;
        request.Reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim();
        request.AnsweredBy = userId;
        request.AnsweredAt = now;
        Audit.Add(db, http, clock, AuditActions.PermissionAnswered, request.WorkspaceId, userId, targetType: "permission_request",
            targetId: id, detail: new { request.Decision, request.ToolName });
        await db.SaveChangesAsync(http.RequestAborted);
        broker.Publish(Broker.Agent(request.AgentId), new StreamMessage(AgentStreamEvents.PermissionAnswer,
            new PermissionAnswerMessage(id, found.Session.ExternalId, request.Decision!, request.Reason)));
        broker.Publish(Broker.Workspace(request.WorkspaceId), new StreamMessage("permission", new { sessionId = request.SessionId, id, status = request.Status }));
        return Results.NoContent();
    }
}
