using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>What an admin sets; a null value clears that attribute. Code: a two-step code when lowering needs a recent sign-in.</summary>
public sealed record AgentRoleRequest(string? Environment, string? Function, string? Code = null);

public sealed record AgentRoleView(Guid AgentId, string? Environment, string? Function, DateTimeOffset? SetAt, Guid? SetBy,
    string? ClaimedEnvironment, string? ClaimedFunction, string? EffectiveEnvironment, string? EffectiveFunction);

/// <summary>
/// The role of an agent (ADR-0008 §3a): only a workspace admin sets it (anyone else gets 404, as for a workspace they cannot see),
/// every change is audited with before and after, and lowering away from production or database needs the re-authentication a
/// grant approval needs. The agent's own claim is never written here; it only tightens the effective role.
/// </summary>
public static class AgentRoleEndpoints
{
    public const string MachineEvent = "machine";

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/agents").RequireAuthorization(Schemes.Session);
        g.MapGet("/{id:guid}/role", Get);
        g.MapPut("/{id:guid}/role", Put);
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, MonitorDb db)
    {
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.Status == AgentStatuses.Active, http.RequestAborted);
        if (agent is null || await Access.MemberAsync(db, http.User.UserId(), agent.WorkspaceId, Roles.Viewer, http.RequestAborted) is null)
        {
            return Http.NotFound();
        }

        return Results.Ok(View(agent));
    }

    private static async Task<IResult> Put(Guid id, AgentRoleRequest req, HttpContext http, MonitorDb db, ApiConfig config, TimeProvider clock,
        Broker broker)
    {
        var userId = http.User.UserId();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id && a.Status == AgentStatuses.Active, http.RequestAborted);
        if (agent is null || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Admin, http.RequestAborted) is null)
        {
            return Http.NotFound();
        }

        if (!MachineRoles.IsEnvironment(req.Environment)) return Http.Invalid("environment", RemoteErrors.InvalidMachineRole);
        if (!MachineRoles.IsFunction(req.Function)) return Http.Invalid("function", RemoteErrors.InvalidMachineRole);
        if (MachineRoles.IsLowering(agent.RoleEnvironment, agent.RoleFunction, req.Environment, req.Function)
            && !await RemoteChecks.ReauthAsync(http, db, config, clock, userId, req.Code))
        {
            return RemoteChecks.ReauthRefused();
        }

        var before = new { environment = agent.RoleEnvironment, function = agent.RoleFunction };
        var now = clock.GetUtcNow();
        // Compare-and-set on the values the decision was made on: a concurrent raise to production is never overwritten without its re-authentication.
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        var changed = await db.Agents
            .Where(a => a.Id == id && a.Status == AgentStatuses.Active && a.RoleEnvironment == before.environment && a.RoleFunction == before.function)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.RoleEnvironment, req.Environment).SetProperty(a => a.RoleFunction, req.Function)
                .SetProperty(a => a.RoleSetAt, now).SetProperty(a => a.RoleSetBy, userId), http.RequestAborted);
        if (changed != 1) return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "conflict");
        Audit.Add(db, http, clock, AuditActions.AgentRoleChanged, agent.WorkspaceId, userId, targetType: "agent", targetId: id,
            detail: new { before, after = new { environment = req.Environment, function = req.Function } });
        await db.SaveChangesAsync(http.RequestAborted);
        await tx.CommitAsync(http.RequestAborted);
        agent.RoleEnvironment = req.Environment;
        agent.RoleFunction = req.Function;
        agent.RoleSetAt = now;
        agent.RoleSetBy = userId;
        broker.Publish(Broker.Workspace(agent.WorkspaceId), new StreamMessage(MachineEvent, new { agentId = id }));
        return Results.Ok(View(agent));
    }

    public static AgentRoleView View(Agent a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new(a.Id, a.RoleEnvironment, a.RoleFunction, a.RoleSetAt, a.RoleSetBy, a.ClaimedEnvironment, a.ClaimedFunction,
            MachineRoles.EffectiveEnvironment(a.RoleEnvironment, a.ClaimedEnvironment), MachineRoles.EffectiveFunction(a.RoleFunction, a.ClaimedFunction));
    }
}
