using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>
/// Fixed agent actions (ADR-0005, "Agent actions from the web"): the machine's owner asks and reads on the web, the agent reports on its own
/// calls. All the rules live in <see cref="AgentActions"/>.
/// </summary>
public static class AgentActionEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var web = api.MapGroup("/agents").RequireAuthorization(Schemes.Session);
        web.MapPost("/{id:guid}/actions", Create);
        web.MapGet("/{id:guid}/actions", List);
        web.MapPut("/{id:guid}/claude", SetClaude);
        api.MapGroup("/agent").RequireAuthorization(Schemes.Agent).AddEndpointFilter(AgentEndpoints.VersionGate)
            .MapPost("/actions/{id:guid}/status", Report);
    }

    private static Task<IResult> Create(Guid id, AgentActionRequest req, HttpContext http, MonitorDb db, ApiConfig config, TimeProvider clock, Broker broker) =>
        AgentActions.CreateAsync(id, req, http, db, config, clock, broker);

    private static Task<IResult> SetClaude(Guid id, AgentClaudeRequest req, HttpContext http, MonitorDb db, ApiConfig config, TimeProvider clock, Broker broker) =>
        AgentActions.SetClaudeControlAsync(id, req, http, db, config, clock, broker);

    private static Task<IResult> List(Guid id, HttpContext http, MonitorDb db, TimeProvider clock) =>
        AgentActions.ListAsync(id, http, db, clock);

    private static Task<IResult> Report(Guid id, AgentActionReport req, HttpContext http, MonitorDb db, TimeProvider clock, Broker broker) =>
        AgentActions.ReportAsync(id, req, http.User.AgentId(), http, db, clock, broker);
}
