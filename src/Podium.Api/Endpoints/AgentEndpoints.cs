using System.Text.Json;
using Podium.Api.AccessPolicies;
using Podium.Api.Approvals;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Ingest;
using Podium.Api.Remote;
using Podium.Api.Security;
using Podium.Api.Streaming;
using Podium.Api.Update;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Podium.Api.Endpoints;

/// <summary>What an agent calls. Every call except the refresh carries the agent's access token.</summary>
public static partial class AgentEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/agent/token/refresh", Refresh).RequireRateLimiting(AuthEndpoints.RateLimitPolicy).AllowAnonymous();
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
        if (os is null || !OsKinds.All.Contains(os)) return Http.Invalid("os", "invalid_os");
        if (arch is not ("arm64" or "x64")) return Http.Invalid("arch", "invalid_arch");
        return catalog.Latest(os, arch) is { } offer ? Results.Ok(offer) : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "no_update");
    }

    private static async Task<IResult> Refresh(RefreshRequest req, HttpContext http, PodiumDb db, ApiConfig config,
        TimeProvider clock, Broker broker)
    {
        // One transaction: the replaced mark on the old token and its successor are stored together (AgentTokens.RefreshAsync).
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        var now = clock.GetUtcNow();
        var (outcome, tokens, agentId, reason) = await AgentTokens.RefreshAsync(db, config, req.RefreshToken, now, http.RequestAborted);
        var notices = new List<Action>();
        if (outcome == AgentTokens.RefreshOutcome.Reused)
        {
            // The row goes to the agent's workspace: the audit log is read per workspace, and a copied token is what its admins must see.
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, http.RequestAborted);
            Audit.Add(db, http, clock, AuditActions.RefreshReuse, agent?.WorkspaceId, agentId: agentId);
            // Its tokens are dead, so the agent is revoked too (no person did it: RevokedBy stays empty), and its remote work ends with it.
            if (agent is { Status: AgentStatuses.Active })
            {
                agent.Status = AgentStatuses.Revoked;
                agent.RevokedAt = now;
                await db.SaveChangesAsync(http.RequestAborted);
                await RemoteCleanup.ForAgentAsync(db, broker, agent.Id, null, now, http.RequestAborted, notices);
            }
        }

        await db.SaveChangesAsync(http.RequestAborted);
        await tx.CommitAsync(http.RequestAborted);
        notices.ForEach(n => n());
        if (outcome == AgentTokens.RefreshOutcome.Reused) broker.Publish(Broker.Agent(agentId!.Value), new StreamMessage(AgentStreamEvents.Revoked, new { }));
        return outcome == AgentTokens.RefreshOutcome.Issued
            ? Results.Ok(tokens)
            : Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "invalid_refresh_token",
                extensions: new Dictionary<string, object?> { ["reason"] = reason });
    }

    private static async Task<IResult> Batch(EventBatch batch, HttpContext http, BatchIngestor ingestor)
    {
        var (outcome, stored) = await ingestor.IngestAsync(http.User.AgentId(), http.User.AgentWorkspaceId(), batch, http.RequestAborted);
        return outcome switch
        {
            IngestOutcome.TooLarge => Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "batch_too_large"),
            IngestOutcome.Unstorable => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: BatchIngestor.Unstorable),
            IngestOutcome.Duplicate => Results.Ok(new BatchAck(batch.BatchSeq, true, 0)),
            IngestOutcome.Stored => Results.Ok(new BatchAck(batch.BatchSeq, false, stored, batch.Events.Count - stored)),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> Heartbeat(HttpContext http, PodiumDb db, TimeProvider clock)
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

    private static async Task<IResult> Settings(HttpContext http, PodiumDb db, ApiConfig config)
    {
        var workspaceId = http.User.AgentWorkspaceId();
        var s = await db.WorkspaceSettings.AsNoTracking().FirstAsync(x => x.WorkspaceId == workspaceId, http.RequestAborted);
        var mine = await db.Agents.AsNoTracking().Where(a => a.Id == http.User.AgentId()).Select(a => new { a.ShareServices, a.AcceptsClaudeControl, a.ClaudeAutoMode, a.ClaudeAutoProject, a.ShareInventory, a.ExecLevelRequest }).FirstOrDefaultAsync(http.RequestAborted);
        // The automatic Claude start (ADR-0014 P3) is only ever told to an agent whose owner has the switch on AND the workspace allows remote work: anything else reads as off.
        var auto = s.RemoteRunsEnabled && mine is { AcceptsClaudeControl: true, ClaudeAutoMode: ClaudeAutoModes.OnMessage or ClaudeAutoModes.Always } ? mine.ClaudeAutoMode! : ClaudeAutoModes.Off;
        var stamp = await AccessPolicyDelivery.StampAsync(db, workspaceId, http.RequestAborted);
        return Results.Ok(new AgentSettings(s.MaskSecrets, s.EventMaxBytes, workspaceId, s.AgentUpdate, s.RemoteRunsEnabled,
            new AlertThresholds(s.AlertCpuPct, s.AlertMemoryPct, s.AlertDiskPct, s.AlertSustainSeconds), s.ClaudeUpdate, mine?.ShareServices ?? false, auto, auto == ClaudeAutoModes.Always ? mine!.ClaudeAutoProject : null,
            mine?.ShareInventory ?? false, stamp, s.EnforceAccessPolicies, ExecLevelChanges.Read<ExecLevelRequest>(mine?.ExecLevelRequest), config.MaxBatchEvents,
            mine?.AcceptsClaudeControl ?? false));
    }

    /// <summary>The agent's own stream: a ready message, then every command and approved run still waiting for it, then new ones as they come.</summary>
    private static async Task<IResult> Stream(HttpContext http, PodiumDb db, Broker broker, TimeProvider clock, ApiConfig config)
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
        var runs = await RunNotices.WaitingForAsync(db, agentId, now, http.RequestAborted, broker);
        var asks = await AskQueries.WaitingForAsync(db, agentId, now, http.RequestAborted);
        var actions = await AgentActions.WaitingForAsync(db, agentId, now, http.RequestAborted);
        var first = new[] { new StreamMessage(AgentStreamEvents.Ready, new { }) }
            .Concat(waiting.Select(c => new StreamMessage(AgentStreamEvents.Command, c)))
            .Concat(runs.Select(r => new StreamMessage(AgentStreamEvents.Run, r)))
            .Concat(asks.Select(a => new StreamMessage(AgentStreamEvents.Ask, a)))
            .Concat(actions.Select(a => new StreamMessage(AgentStreamEvents.Action, a)));
        return Sse.Stream(subscription, first, config.StreamPingEvery, http.RequestAborted);
    }

    private static async Task<IResult> CommandStatus(Guid id, CommandStatusUpdate req, HttpContext http, PodiumDb db,
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
        var was = command.Status;
        command.Status = req.Status!;
        command.Result = req.Result is { Length: > 500 } r ? r[..500] : req.Result;
        if (req.Status == CommandStatuses.Delivered)
        {
            command.DeliveredAt ??= now;
            // Delivered with a transport: written into the session, not shown yet. Empty: that was given up. Anything else is ignored.
            if (req.Transport == "") command.Transport = null;
            else if (req.Transport is CommandTransports.Inbox or CommandTransports.Channel) command.Transport = req.Transport;
        }
        else
        {
            command.AppliedAt = AppliedMoment(req.At, command.CreatedAt, now, config.ClockSkewMax);
            // An unknown value is dropped, not refused: a newer agent may name a way this API does not know yet.
            if (req.Status == CommandStatuses.Applied && req.Transport is { } transport && CommandTransports.All.Contains(transport)) command.Transport = transport;
        }
        // Compare-and-set on the status read above: a cancel (or another report) that changed it since wins, and this report is a 409.
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        var moved = await db.SessionCommands.Where(c => c.Id == id && c.Status == was)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, command.Status), http.RequestAborted);
        if (moved == 0) return Results.Conflict();
        await db.SaveChangesAsync(http.RequestAborted);
        await tx.CommitAsync(http.RequestAborted);
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

}
