using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>
/// Remote runs as agents call them (ADR-0004): a requester asks, reads and cancels its own runs; a target reports the
/// status and output of runs sent to it. Every call is the agent's own, checked against the run's requester or target.
/// </summary>
public static class AgentRunEndpoints
{
    public const int OutputPageMax = 50;

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/agent").RequireAuthorization(Schemes.Agent).AddEndpointFilter(AgentEndpoints.VersionGate);
        g.MapPost("/runs", Create);
        g.MapGet("/runs/{id:guid}", Get);
        g.MapGet("/runs/{id:guid}/output", Output);
        g.MapPost("/runs/{id:guid}/cancel", Cancel);
        g.MapPost("/runs/{id:guid}/status", Status);
        g.MapPost("/runs/{id:guid}/output", Report);
    }

    private static async Task<IResult> Create(RunCreate req, HttpContext http, RunCreator creator)
    {
        var result = await creator.CreateAsync(req, http.User.AgentId(), http.User.UserId(), http.User.AgentWorkspaceId(), http);
        return result.Run is { } run ? Results.Ok(run) : Results.Problem(statusCode: result.Status, title: result.Error);
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, MonitorDb db)
    {
        var view = await RunQueries.ForRequesterAsync(db, id, http.User.UserId(), http.RequestAborted);
        return view is null ? Http.NotFound() : Results.Ok(view);
    }

    private static async Task<IResult> Output(Guid id, int? after, int? limit, HttpContext http, MonitorDb db)
    {
        var view = await RunQueries.ForRequesterAsync(db, id, http.User.UserId(), http.RequestAborted);
        if (view is null) return Http.NotFound();
        var page = await RunQueries.OutputAsync(db, id, after ?? -1, Math.Clamp(limit ?? OutputPageMax, 1, OutputPageMax),
            RunStatuses.Final.Contains(view.Status), http.RequestAborted);
        return Results.Ok(page);
    }

    private static Task<IResult> Cancel(Guid id, HttpContext http, RunDecisions decisions) =>
        decisions.CancelAsync(id, http.User.UserId(), http.User.AgentId(), http);

    private static Task<IResult> Status(Guid id, RunStatusUpdate req, HttpContext http, RunReports reports) =>
        reports.StatusAsync(id, req, http.User.AgentId(), http);

    private static Task<IResult> Report(Guid id, List<RunOutputChunk> chunks, HttpContext http, RunReports reports) =>
        reports.OutputAsync(id, chunks, http.User.AgentId(), http);
}
