using System.Text.Json;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Ingest;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Api.Update;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>What an agent calls. Every call except the refresh carries the agent's access token.</summary>
public static class AgentEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/agent/token/refresh", Refresh).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        // Outside the version gate on purpose: an agent below the minimum version must still be able to fetch its update.
        api.MapGet("/agent/latest", Latest).RequireAuthorization(Schemes.Agent);
        var g = api.MapGroup("/agent").RequireAuthorization(Schemes.Agent).AddEndpointFilter(VersionGate);
        g.MapPost("/batches", Batch).WithMetadata(new RequestSizeLimitAttributeShim());
        g.MapPost("/heartbeat", Heartbeat);
        g.MapGet("/settings", Settings);
        g.MapGet("/stream", Stream);
        g.MapPost("/commands/{id:guid}/status", CommandStatus);
        g.MapPost("/permission-requests", CreatePermission);
        g.MapGet("/permission-requests/{id:guid}", GetPermission);
    }

    /// <summary>Old agents in the field: below the configured minimum version the API answers 426.</summary>
    internal static async ValueTask<object?> VersionGate(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var config = ctx.HttpContext.RequestServices.GetRequiredService<ApiConfig>();
        var header = ctx.HttpContext.Request.Headers[AgentHeaders.Version].ToString();
        return !Version.TryParse(header, out var v) || v < config.MinimumAgentVersion
            ? DeviceEndpoints.UpgradeRequired()
            : await next(ctx);
    }

    /// <summary>The newest signed build for the caller's OS and CPU; the agent checks the signature, the hash and the version itself.</summary>
    private static IResult Latest(string? os, string? arch, UpdateCatalog catalog)
    {
        if (os is not ("macos" or "windows")) return Http.Invalid("os", "invalid_os");
        if (arch is not ("arm64" or "x64")) return Http.Invalid("arch", "invalid_arch");
        return catalog.Latest(os, arch) is { } offer ? Results.Ok(offer) : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "no_update");
    }

    private static async Task<IResult> Refresh(RefreshRequest req, HttpContext http, MonitorDb db, ApiConfig config,
        TimeProvider clock, Broker broker)
    {
        var (outcome, tokens, agentId) = await AgentTokens.RefreshAsync(db, config, req.RefreshToken, clock.GetUtcNow(), http.RequestAborted);
        if (outcome == AgentTokens.RefreshOutcome.Reused)
        {
            Audit.Add(db, http, clock, AuditActions.RefreshReuse, agentId: agentId);
            broker.Publish(Broker.Agent(agentId!.Value), new StreamMessage(AgentStreamEvents.Revoked, new { }));
        }

        await db.SaveChangesAsync(http.RequestAborted);
        return outcome == AgentTokens.RefreshOutcome.Issued
            ? Results.Ok(tokens)
            : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "invalid_refresh_token");
    }

    private static async Task<IResult> Batch(EventBatch batch, HttpContext http, BatchIngestor ingestor)
    {
        var (outcome, stored) = await ingestor.IngestAsync(http.User.AgentId(), http.User.AgentWorkspaceId(), batch, http.RequestAborted);
        return outcome switch
        {
            IngestOutcome.TooLarge => Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "batch_too_large"),
            IngestOutcome.Duplicate => Results.Ok(new BatchAck(batch.BatchSeq, true, 0)),
            IngestOutcome.Stored => Results.Ok(new BatchAck(batch.BatchSeq, false, stored)),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> Heartbeat(HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var agentId = http.User.AgentId();
        var agent = await db.Agents.FirstAsync(a => a.Id == agentId, http.RequestAborted);
        agent.LastHeartbeatAt = now;
        agent.Version = http.Request.Headers[AgentHeaders.Version].ToString();
        await db.Machines.Where(m => m.Id == agent.MachineId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastSeenAt, now), http.RequestAborted);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> Settings(HttpContext http, MonitorDb db)
    {
        var workspaceId = http.User.AgentWorkspaceId();
        var s = await db.WorkspaceSettings.AsNoTracking().FirstAsync(x => x.WorkspaceId == workspaceId, http.RequestAborted);
        return Results.Ok(new AgentSettings(s.MaskSecrets, s.EventMaxBytes, workspaceId, s.AgentUpdate, s.RemoteRunsEnabled,
            new AlertThresholds(s.AlertCpuPct, s.AlertMemoryPct, s.AlertDiskPct, s.AlertSustainSeconds), s.ClaudeUpdate));
    }

    /// <summary>The agent's own stream: a ready message, then every command and approved run still waiting for it, then new ones as they come.</summary>
    private static async Task<IResult> Stream(HttpContext http, MonitorDb db, Broker broker, TimeProvider clock)
    {
        var agentId = http.User.AgentId();
        var subscription = broker.Subscribe(Broker.Agent(agentId));
        var now = clock.GetUtcNow();
        var waiting = await (from c in db.SessionCommands.AsNoTracking()
                             join s in db.HarnessSessions.AsNoTracking() on c.SessionId equals s.Id
                             where c.AgentId == agentId && (c.Status == CommandStatuses.Queued || c.Status == CommandStatuses.Delivered)
                                   && c.ExpiresAt > now
                             orderby c.CreatedAt
                             select new AgentCommandMessage(c.Id, c.SessionId, s.ExternalId, c.Kind, c.Body, c.ExpiresAt))
            .ToListAsync(http.RequestAborted);
        // "ready" first: the response headers go out with the first message, and an idle stream would otherwise look unconnected for 20 s.
        var runs = await RunNotices.WaitingForAsync(db, agentId, now, http.RequestAborted);
        var first = new[] { new StreamMessage(AgentStreamEvents.Ready, new { }) }
            .Concat(waiting.Select(c => new StreamMessage(AgentStreamEvents.Command, c)))
            .Concat(runs.Select(r => new StreamMessage(AgentStreamEvents.Run, r)));
        return Sse.Stream(subscription, first, http.RequestAborted);
    }

    private static async Task<IResult> CommandStatus(Guid id, CommandStatusUpdate req, HttpContext http, MonitorDb db,
        TimeProvider clock, Broker broker, ApiConfig config)
    {
        if (!CommandStatuses.FromAgent.Contains(req.Status ?? "")) return Http.Invalid("status", "invalid_status");
        var agentId = http.User.AgentId();
        var command = await db.SessionCommands.FirstOrDefaultAsync(c => c.Id == id && c.AgentId == agentId, http.RequestAborted);
        if (command is null) return Http.NotFound();
        if (command.Status is CommandStatuses.Applied or CommandStatuses.Failed or CommandStatuses.Cancelled or CommandStatuses.Expired)
        {
            return Results.Conflict();
        }

        var now = clock.GetUtcNow();
        command.Status = req.Status!;
        command.Result = req.Result is { Length: > 500 } r ? r[..500] : req.Result;
        if (req.Status == CommandStatuses.Delivered) command.DeliveredAt = now;
        else command.AppliedAt = AppliedMoment(req.At, command.CreatedAt, now, config.ClockSkewMax);
        await db.SaveChangesAsync(http.RequestAborted);
        broker.Publish(Broker.Workspace(command.WorkspaceId),
            new StreamMessage("command", new { sessionId = command.SessionId, id, status = command.Status }));
        return Results.NoContent();
    }

    /// <summary>
    /// When the hook handed the command to the session, as the agent saw it (the report itself can come seconds later).
    /// Believed unless it is older than the command itself (give or take the allowed clock skew); never later than now.
    /// </summary>
    internal static DateTimeOffset AppliedMoment(DateTimeOffset? reported, DateTimeOffset created, DateTimeOffset now, TimeSpan skew) =>
        reported is { } at && at >= created - skew ? (at < now ? at : now) : now;

    private static async Task<IResult> CreatePermission(PermissionRequestCreate req, HttpContext http, MonitorDb db,
        ApiConfig config, TimeProvider clock, Broker broker)
    {
        var agentId = http.User.AgentId();
        var session = await db.HarnessSessions.FirstOrDefaultAsync(
            s => s.AgentId == agentId && s.HarnessKind == req.HarnessKind && s.ExternalId == req.SessionExternalId, http.RequestAborted);
        if (session is null) return Http.NotFound();
        if (req.ToolName is not { Length: > 0 and <= 200 } || req.ToolInput.ValueKind != JsonValueKind.Object)
        {
            return Http.Invalid("toolName", "invalid");
        }

        var now = clock.GetUtcNow();
        var wait = Math.Clamp(req.WaitSeconds, 1, config.PermissionWaitMaxSeconds);
        var request = new PermissionRequest
        {
            WorkspaceId = session.WorkspaceId,
            SessionId = session.Id,
            AgentId = agentId,
            ToolName = req.ToolName,
            ToolInput = JsonDocument.Parse(req.ToolInput.GetRawText()),
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(wait),
        };
        db.PermissionRequests.Add(request);
        session.Status = SessionStatuses.Waiting;
        await db.SaveChangesAsync(http.RequestAborted);
        broker.Publish(Broker.Workspace(session.WorkspaceId),
            new StreamMessage("permission", new { sessionId = session.Id, id = request.Id, status = request.Status }));
        return Results.Ok(new PermissionRequestCreated(request.Id, request.ExpiresAt));
    }

    private static async Task<IResult> GetPermission(Guid id, HttpContext http, MonitorDb db)
    {
        var agentId = http.User.AgentId();
        var row = await (from p in db.PermissionRequests.AsNoTracking()
                         join s in db.HarnessSessions.AsNoTracking() on p.SessionId equals s.Id
                         where p.Id == id && p.AgentId == agentId
                         select new { p, s.ExternalId }).FirstOrDefaultAsync(http.RequestAborted);
        return row is null
            ? Http.NotFound()
            : Results.Ok(new { row.p.Status, answer = row.p.Decision is null ? null : new PermissionAnswerMessage(row.p.Id, row.ExternalId, row.p.Decision, row.p.Reason) });
    }
}

/// <summary>Batches may be larger than the default request limit; the ingestor enforces its own event count.</summary>
public sealed class RequestSizeLimitAttributeShim : Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata
{
    public long? MaxRequestBodySize => ApiConfig.BatchBodyLimit;
}
