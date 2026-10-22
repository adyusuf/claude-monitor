using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Remote;
using Podium.Api.Security;
using Podium.Api.Streaming;
using Podium.Contracts;

namespace Podium.Api.Endpoints;

/// <summary>
/// An agent's machine reports, and the workspace's machines as a requester's MCP tools read them (ADR-0005). Reads
/// need the agent's user to be at least a member; a viewer's agent reports but sees no other machine.
/// </summary>
public static class AgentMachineEndpoints
{
    public const int AlertsMax = 100;

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/agent").RequireAuthorization(Schemes.Agent).AddEndpointFilter(AgentEndpoints.VersionGate);
        g.MapPut("/profile", Profile);
        g.MapPost("/metrics", Metrics);
        g.MapPost("/alerts", Alerts);
        g.MapGet("/machines", Machines);
        g.MapGet("/machines/{id:guid}/detail", Detail);
        g.MapGet("/machines/{id:guid}/metrics", MachineMetrics);
        g.MapGet("/alerts", ListAlerts);
    }

    private static async Task<IResult> Profile(AgentProfile req, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        await MachineReports.ProfileAsync(db, http.User.AgentId(), req, clock.GetUtcNow(), http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> Metrics(MetricsReport req, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock)
    {
        var stored = await MachineReports.MetricsAsync(db, config, http.User.AgentId(), http.User.AgentWorkspaceId(), req,
            clock.GetUtcNow(), http.RequestAborted);
        return Results.Ok(new { stored });
    }

    private static async Task<IResult> Alerts(List<AlertReport> req, HttpContext http, PodiumDb db, Broker broker, TimeProvider clock)
    {
        await MachineReports.AlertsAsync(db, broker, http.User.AgentId(), http.User.AgentWorkspaceId(), req, clock.GetUtcNow(),
            http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> Machines(HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock)
    {
        var ws = http.User.AgentWorkspaceId();
        if (await Access.MemberAsync(db, http.User.UserId(), ws, Roles.Member, http.RequestAborted) is null) return Http.NotFound();
        return Results.Ok(await MachineQueries.MachinesAsync(db, config, ws, clock.GetUtcNow(), http.RequestAborted));
    }

    private static async Task<IResult> Detail(Guid id, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock)
    {
        var ws = http.User.AgentWorkspaceId();
        var userId = http.User.UserId();
        if (await Access.MemberAsync(db, userId, ws, Roles.Member, http.RequestAborted) is null) return Http.NotFound();
        var admin = await Access.MemberAsync(db, userId, ws, Roles.Admin, http.RequestAborted) is not null;
        return await MachineDetails.GetAsync(db, config, ws, id, userId, admin, clock.GetUtcNow(), http.RequestAborted) is { } detail
            ? Results.Ok(detail)
            : Http.NotFound();
    }

    private static async Task<IResult> MachineMetrics(Guid id, int? minutes, HttpContext http, PodiumDb db, ApiConfig config,
        TimeProvider clock)
    {
        var ws = http.User.AgentWorkspaceId();
        if (await Access.MemberAsync(db, http.User.UserId(), ws, Roles.Member, http.RequestAborted) is null) return Http.NotFound();
        return Results.Ok(await MachineQueries.MetricsAsync(db, config, ws, id, minutes ?? 60, clock.GetUtcNow(), http.RequestAborted));
    }

    private static async Task<IResult> ListAlerts(Guid? agent, bool? resolved, HttpContext http, PodiumDb db)
    {
        var ws = http.User.AgentWorkspaceId();
        if (await Access.MemberAsync(db, http.User.UserId(), ws, Roles.Member, http.RequestAborted) is null) return Http.NotFound();
        return Results.Ok(await MachineQueries.AlertsAsync(db, ws, agent, resolved ?? false, AlertsMax, http.RequestAborted));
    }
}
