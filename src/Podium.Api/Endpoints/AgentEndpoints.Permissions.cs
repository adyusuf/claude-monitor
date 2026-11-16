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

public static partial class AgentEndpoints
{
    private static async Task<IResult> CreatePermission(PermissionRequestCreate req, HttpContext http, PodiumDb db,
        ApiConfig config, TimeProvider clock, Broker broker, ILoggerFactory logs)
    {
        var agentId = http.User.AgentId();
        var session = await db.HarnessSessions.FirstOrDefaultAsync(
            s => s.AgentId == agentId && s.HarnessKind == req.HarnessKind && s.ExternalId == req.SessionExternalId, http.RequestAborted);
        if (session is null) return Http.NotFound();
        if (req.ToolName is not { Length: > 0 and <= 200 } || req.ToolInput.ValueKind != JsonValueKind.Object)
        {
            return Http.Invalid("toolName", "invalid");
        }

        if (!PermissionRequestKeys.Valid(req.ClientKey)) return Http.Invalid("clientKey", RunRules.BadClientKey);
        // The same ask sent again after a lost response (F-188) gets the request it made, never a second open one.
        if (await PermissionRequestKeys.FindAsync(db, agentId, req.ClientKey, req.InlineAnswer, http.RequestAborted) is { } again) return Results.Ok(again);
        var now = clock.GetUtcNow();
        var wait = Math.Clamp(req.WaitSeconds, 1, config.PermissionWaitMaxSeconds);
        var request = new PermissionRequest
        {
            WorkspaceId = session.WorkspaceId,
            SessionId = session.Id,
            AgentId = agentId,
            ToolName = req.ToolName,
            ToolInput = JsonDocument.Parse(PermissionToolInputs.Capped(req.ToolInput.GetRawText())), // an older agent sends it whole (F-189)
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(wait),
            RepoId = session.RepoId,
            AccessScope = AccessScopes.All.Contains(req.AccessScope ?? "") ? req.AccessScope : null, // an unknown value is dropped, so no path rule
            ClientKey = req.ClientKey,
        };
        db.PermissionRequests.Add(request);

        // The rule's use is counted in the same transaction as the request it answers (F-162).
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        // An approval rule answers at once, but only to an agent that reads the answer in this response (ADR-0019 §7, §8).
        var agent = req.InlineAnswer ? await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, http.RequestAborted) : null;
        var rule = agent is null ? null
            : await ApprovalRules.MatchAsync(db, agent, request.RepoId, req.ToolName, req.ToolInput, request.AccessScope, now, http.RequestAborted,
                logs.CreateLogger(typeof(ApprovalRules)));
        // The use is a conditional write on a rule still live (as a grant's is), so a rule revoked or ended a moment ago allows nothing and
        // requests at the same moment all count; a request it does not allow waits for the owner like any other.
        if (rule is not null && !await ApprovalRules.CountUseAsync(db, rule.Id, now, http.RequestAborted)) rule = null;
        PermissionAnswerMessage? answer = null;
        if (rule is not null)
        {
            request.Status = PermissionStatuses.Answered;
            request.Decision = PermissionDecisions.Allow;
            request.AnsweredBy = rule.UserId;
            request.AnsweredAt = now;
            request.AnsweredByRule = rule.Id;
            Audit.Add(db, http, clock, AuditActions.PermissionAutoAllowed, session.WorkspaceId, rule.UserId, targetType: "permission_request",
                targetId: request.Id, detail: new { rule = rule.Id, request.ToolName });
            answer = new PermissionAnswerMessage(request.Id, session.ExternalId, PermissionDecisions.Allow, null);
        }
        else if (session.Status != SessionStatuses.Ended)
        {
            session.Status = SessionStatuses.Waiting; // an ended session stays ended, as in the event projector
        }

        try
        {
            await db.SaveChangesAsync(http.RequestAborted);
            await tx.CommitAsync(http.RequestAborted);
        }
        catch (DbUpdateException e) when (req.ClientKey is not null && e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The same key at the same moment (ix_permission_requests_agent_id_client_key): the other create won; its request is the answer, this one
            // and the rule use it counted are rolled back.
            await tx.RollbackAsync(http.RequestAborted);
            db.ChangeTracker.Clear();
            return Results.Ok(await PermissionRequestKeys.FindAsync(db, agentId, req.ClientKey, req.InlineAnswer, http.RequestAborted));
        }

        broker.Publish(Broker.Workspace(session.WorkspaceId),
            new StreamMessage("permission", new { sessionId = session.Id, id = request.Id, status = request.Status }));
        return Results.Ok(new PermissionRequestCreated(request.Id, request.ExpiresAt, answer));
    }

    /// <summary>
    /// OBSOLETE (F-196): nothing in the product calls this any more, the answer reaches the agent on its stream. It stays, and keeps returning real
    /// values, for older agents (rule 4); every answer carries <c>Deprecation: true</c>. The answer carries the question tool's chosen options too
    /// (stored with it since F-196), so a poller loses nothing; an answer from before the column has none.
    /// </summary>
    private static async Task<IResult> GetPermission(Guid id, HttpContext http, PodiumDb db)
    {
        http.Response.Headers["Deprecation"] = "true";
        var agentId = http.User.AgentId();
        var row = await (from p in db.PermissionRequests.AsNoTracking()
                         join s in db.HarnessSessions.AsNoTracking() on p.SessionId equals s.Id
                         where p.Id == id && p.AgentId == agentId
                         select new { p, s.ExternalId }).FirstOrDefaultAsync(http.RequestAborted);
        return row is null
            ? Http.NotFound()
            : Results.Ok(new { row.p.Status, answer = PermissionRequestKeys.AnswerOf(row.p, row.ExternalId) });
    }
}

/// <summary>Batches may be larger than the default request limit; the ingestor enforces its own event count.</summary>
public sealed class RequestSizeLimitAttributeShim : Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata
{
    public long? MaxRequestBodySize => ApiConfig.BatchBodyLimit;
}
