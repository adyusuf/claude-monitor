using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Endpoints;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// "Start the test" for a project on a machine (ADR-0011 §7a). The command is the one the person at the machine set and the agent reported with the folder; the web only
/// names the project. It becomes an ordinary argv run of kind test with the machine as its own requester, so everything about a run applies unchanged: the workspace
/// switch, the target's execution level, grants and jobs, the owner's approval, output, retention and the link to an issue. Only the machine's owner may ask.
/// </summary>
public static class TestRuns
{
    public const int TimeoutSeconds = 1800;
    public const string Reason = "Test started from Podium";

    /// <summary>A reported command older than this is not offered or started: the folder may have been removed from the machine's list since.</summary>
    public static readonly TimeSpan FactsMaxAge = TimeSpan.FromDays(1);

    public static async Task<IResult> StartAsync(Guid agentId, TestRunRequest req, HttpContext http, MonitorDb db, RunCreator creator, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        var userId = http.User.UserId();
        var ct = http.RequestAborted;
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId && a.Status == AgentStatuses.Active, ct);
        if (agent is null || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Viewer, ct) is null) return Http.NotFound();
        if (agent.UserId != userId) return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: AgentActionErrors.OwnerOnly);
        if (req.ProjectKey is not { Length: >= 1 and <= RunKinds.ProjectKeyMax } key || key.Any(char.IsControl)) return Http.Invalid("projectKey", AgentActionErrors.InvalidProject);

        var oldest = clock.GetUtcNow() - FactsMaxAge;
        var project = await db.MachineProjects.AsNoTracking().FirstOrDefaultAsync(p => p.AgentId == agentId && p.ProjectKey == key && p.LastSeenAt >= oldest, ct);
        if (project is not { TestArgv: { } argv, TestCwd: { } cwd }) return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: AgentActionErrors.NoTestCommand);

        var run = new RunCreate($"podium-test-{Guid.NewGuid():N}", agentId, RunModes.Argv, argv, null, cwd, TimeoutSeconds, Reason, ProjectKey: key, Kind: RunKinds.Test);
        var result = await creator.CreateAsync(run, agentId, userId, agent.WorkspaceId, http);
        return result.Run is { } created ? Results.Ok(created) : Results.Problem(statusCode: result.Status, title: result.Error);
    }
}
