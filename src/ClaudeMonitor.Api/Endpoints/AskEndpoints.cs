using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>The machine owner's choice to take asks from their other machines. Code: a two-step code when turning it on needs a recent sign-in.</summary>
public sealed record AgentAsksRequest(bool Accept, string? Code = null);

public sealed record AgentAsksView(Guid AgentId, bool Accept, DateTimeOffset? SetAt, Guid? SetBy);

/// <summary>
/// Asks across machines as agents and the web call them (ADR-0008 §3f). An agent creates, reads and cancels its own asks and answers or reports on the ones
/// sent to it; the machine's owner turns the opt-in on (with the re-authentication of a grant) or off (which cancels the open asks) and reads the list.
/// </summary>
public static class AskEndpoints
{
    public const int ListMax = 50;

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/agent").RequireAuthorization(Schemes.Agent).AddEndpointFilter(AgentEndpoints.VersionGate);
        g.MapPost("/asks", Create);
        g.MapGet("/asks/{id:guid}", Get);
        g.MapPost("/asks/{id:guid}/cancel", Cancel);
        g.MapPost("/asks/{id:guid}/status", Status);
        g.MapPost("/asks/{id:guid}/answer", Answer);
        var web = api.MapGroup("/agents").RequireAuthorization(Schemes.Session);
        web.MapPut("/{id:guid}/asks", Accept);
        web.MapGet("/{id:guid}/asks", List);
    }

    private static async Task<IResult> Create(AskCreate req, HttpContext http, AskCreator creator)
    {
        var result = await creator.CreateAsync(req, http.User.AgentId(), http.User.UserId(), http.User.AgentWorkspaceId(), http);
        return result.Ask is { } ask ? Results.Ok(ask) : Results.Problem(statusCode: result.Status, title: result.Error);
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, MonitorDb db)
    {
        var view = await AskQueries.ForUserAsync(db, id, http.User.UserId(), http.RequestAborted);
        return view is null ? Http.NotFound() : Results.Ok(view);
    }

    private static Task<IResult> Cancel(Guid id, HttpContext http, AskReports reports) => reports.CancelAsync(id, http.User.UserId(), http.User.AgentId(), http);

    private static Task<IResult> Status(Guid id, AskStatusUpdate req, HttpContext http, AskReports reports) => reports.StatusAsync(id, req, http.User.AgentId(), http);

    private static Task<IResult> Answer(Guid id, AskAnswer req, HttpContext http, AskReports reports) => reports.AnswerAsync(id, req, http.User.AgentId(), http);

    private static async Task<IResult> Accept(Guid id, AgentAsksRequest req, HttpContext http, MonitorDb db, ApiConfig config, TimeProvider clock, Broker broker)
    {
        var userId = http.User.UserId();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id && a.Status == AgentStatuses.Active, http.RequestAborted);
        if (agent is null || agent.UserId != userId || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        if (req.Accept && !agent.AcceptsAsks && !await RemoteChecks.ReauthAsync(http, db, config, clock, userId, req.Code)) return RemoteChecks.ReauthRefused();
        var was = agent.AcceptsAsks;
        var now = clock.GetUtcNow();
        agent.AcceptsAsks = req.Accept;
        agent.AcceptsAsksSetAt = now;
        agent.AcceptsAsksSetBy = userId;
        Audit.Add(db, http, clock, AuditActions.AgentAsksChanged, agent.WorkspaceId, userId, targetType: "agent", targetId: id, detail: new { before = was, after = req.Accept });
        await db.SaveChangesAsync(http.RequestAborted);
        if (was && !req.Accept) await AskReports.CancelOpenAsync(db, broker, now, a => a.TargetAgentId == id, http.RequestAborted);
        broker.Publish(Broker.Workspace(agent.WorkspaceId), new StreamMessage(AgentRoleEndpoints.MachineEvent, new { agentId = id }));
        return Results.Ok(new AgentAsksView(id, agent.AcceptsAsks, agent.AcceptsAsksSetAt, agent.AcceptsAsksSetBy));
    }

    private static async Task<IResult> List(Guid id, HttpContext http, MonitorDb db)
    {
        var userId = http.User.UserId();
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId, http.RequestAborted);
        if (agent is null || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        return Results.Ok(await AskQueries.ForTargetAsync(db, id, userId, ListMax, http.RequestAborted));
    }
}
