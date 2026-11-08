using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Remote;
using Podium.Api.Security;
using Podium.Api.Streaming;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>
/// What an admin sets; a null value clears that attribute. Code: a two-step code when lowering needs a recent sign-in. Environments/Functions
/// (08/11/2026) carry every value of the role; when either is present both are read as sets (null or empty clears) and the single values are
/// ignored, otherwise the single values are a set of one, as before.
/// </summary>
public sealed record AgentRoleRequest(string? Environment, string? Function, string? Code = null,
    IReadOnlyList<string>? Environments = null, IReadOnlyList<string>? Functions = null);

/// <summary>Environment and Function are the strictest value of each set (what every single-role rule reads); Environments/Functions are the sets.</summary>
public sealed record AgentRoleView(Guid AgentId, string? Environment, string? Function, DateTimeOffset? SetAt, Guid? SetBy,
    string? ClaimedEnvironment, string? ClaimedFunction, string? EffectiveEnvironment, string? EffectiveFunction,
    IReadOnlyList<string>? Environments = null, IReadOnlyList<string>? Functions = null);

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

    private static async Task<IResult> Get(Guid id, HttpContext http, PodiumDb db)
    {
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.Status == AgentStatuses.Active, http.RequestAborted);
        if (agent is null || await Access.MemberAsync(db, http.User.UserId(), agent.WorkspaceId, Roles.Viewer, http.RequestAborted) is null)
        {
            return Http.NotFound();
        }

        return Results.Ok(View(agent));
    }

    private static async Task<IResult> Put(Guid id, AgentRoleRequest req, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock,
        Broker broker)
    {
        var userId = http.User.UserId();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id && a.Status == AgentStatuses.Active, http.RequestAborted);
        if (agent is null || await Access.MemberAsync(db, userId, agent.WorkspaceId, Roles.Admin, http.RequestAborted) is null)
        {
            return Http.NotFound();
        }

        var sets = req.Environments is not null || req.Functions is not null;
        if (sets ? !MachineRoles.IsEnvironments(req.Environments) : !MachineRoles.IsEnvironment(req.Environment)) return Http.Invalid("environment", RemoteErrors.InvalidMachineRole);
        if (sets ? !MachineRoles.IsFunctions(req.Functions) : !MachineRoles.IsFunction(req.Function)) return Http.Invalid("function", RemoteErrors.InvalidMachineRole);
        var environments = MachineRoles.Normalize(sets ? req.Environments : req.Environment is null ? null : [req.Environment], MachineRoles.EnvironmentOrder);
        var functions = MachineRoles.Normalize(sets ? req.Functions : req.Function is null ? null : [req.Function], MachineRoles.FunctionOrder);
        var environment = MachineRoles.Strictest(environments, MachineRoles.EnvironmentOrder);
        var function = MachineRoles.Strictest(functions, MachineRoles.FunctionOrder);
        // Lowering is judged on the strictest values: dropping production (or database) from the set is lowering, adding a looser value is not.
        if (MachineRoles.IsLowering(agent.RoleEnvironment, agent.RoleFunction, environment, function)
            && !await RemoteChecks.ReauthAsync(http, db, config, clock, userId, req.Code))
        {
            return RemoteChecks.ReauthRefused();
        }

        var before = new
        {
            environment = agent.RoleEnvironment, function = agent.RoleFunction,
            environments = MachineRoles.SetOf(agent.RoleEnvironments, agent.RoleEnvironment), functions = MachineRoles.SetOf(agent.RoleFunctions, agent.RoleFunction),
        };
        var now = clock.GetUtcNow();
        // Compare-and-set on the values the decision was made on: a concurrent raise to production is never overwritten without its re-authentication.
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        if (!await TrySetAsync(db, id, before.environment, before.function, environment, function, now, userId, http.RequestAborted, environments, functions))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "conflict");
        }

        Audit.Add(db, http, clock, AuditActions.AgentRoleChanged, agent.WorkspaceId, userId, targetType: "agent", targetId: id,
            detail: new { before, after = new { environment, function, environments, functions } });
        await db.SaveChangesAsync(http.RequestAborted);
        await tx.CommitAsync(http.RequestAborted);
        agent.RoleEnvironment = environment;
        agent.RoleFunction = function;
        agent.RoleEnvironments = environments?.ToList();
        agent.RoleFunctions = functions?.ToList();
        agent.RoleSetAt = now;
        agent.RoleSetBy = userId;
        broker.Publish(Broker.Workspace(agent.WorkspaceId), new StreamMessage(MachineEvent, new { agentId = id }));
        return Results.Ok(View(agent));
    }

    /// <summary>
    /// Writes the role only while the row still holds the strictest values the decision was made on; false when they changed in between. The
    /// sets default to the single values (a set of one); the single values written are the strictest of the sets.
    /// </summary>
    public static async Task<bool> TrySetAsync(PodiumDb db, Guid id, string? beforeEnvironment, string? beforeFunction, string? environment,
        string? function, DateTimeOffset now, Guid userId, CancellationToken ct, IReadOnlyList<string>? environments = null, IReadOnlyList<string>? functions = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var envs = (environments ?? (environment is null ? null : [environment]))?.ToList();
        var fns = (functions ?? (function is null ? null : [function]))?.ToList();
        var changed = await db.Agents
            .Where(a => a.Id == id && a.Status == AgentStatuses.Active && a.RoleEnvironment == beforeEnvironment && a.RoleFunction == beforeFunction)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.RoleEnvironment, environment).SetProperty(a => a.RoleFunction, function)
                .SetProperty(a => a.RoleEnvironments, envs).SetProperty(a => a.RoleFunctions, fns)
                .SetProperty(a => a.RoleSetAt, now).SetProperty(a => a.RoleSetBy, userId), ct);
        return changed == 1;
    }

    public static AgentRoleView View(Agent a)
    {
        ArgumentNullException.ThrowIfNull(a);
        return new(a.Id, a.RoleEnvironment, a.RoleFunction, a.RoleSetAt, a.RoleSetBy, a.ClaimedEnvironment, a.ClaimedFunction,
            MachineRoles.EffectiveEnvironment(a.RoleEnvironment, a.ClaimedEnvironment), MachineRoles.EffectiveFunction(a.RoleFunction, a.ClaimedFunction),
            MachineRoles.SetOf(a.RoleEnvironments, a.RoleEnvironment), MachineRoles.SetOf(a.RoleFunctions, a.RoleFunction));
    }
}
