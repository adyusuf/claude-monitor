using Podium.Api.AccessPolicies;
using Podium.Api.Data;
using Podium.Api.Security;

namespace Podium.Api.Endpoints;

/// <summary>
/// What an agent fetches of the access policies (ADR-0020 §4) when the stamp in its settings moved: the policies of the repositories its machine
/// reports, tombstones included. Only for an agent whose user is still a member of its workspace; anything else answers 404, which the agent reads
/// as "keep what you have".
/// </summary>
public static class AgentAccessEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/agent").RequireAuthorization(Schemes.Agent).AddEndpointFilter(AgentEndpoints.VersionGate);
        g.MapGet("/access-policies", Policies);
    }

    private static async Task<IResult> Policies(HttpContext http, PodiumDb db)
    {
        var workspaceId = http.User.AgentWorkspaceId();
        if (await Access.MemberAsync(db, http.User.UserId(), workspaceId, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        return Results.Ok(await AccessPolicyDelivery.PoliciesAsync(db, http.User.AgentId(), workspaceId, http.RequestAborted));
    }
}
