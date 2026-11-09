using Podium.Api.AccessPolicies;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Projects;
using Podium.Api.Security;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>
/// The live access policy a repo key falls under, for the session page: its project, version, whether it is signed, whether it allows shell runs, and
/// its document; Enforced is the workspace switch. No live policy answers 404 (the default applies when the switch is on or the machine is strict).
/// </summary>
public sealed record AccessKeySummary(Guid ProjectId, string ProjectName, int Version, bool IsSigned, bool Shell, string ProjectKey, bool Enforced,
    AccessPolicyDocument Document);

/// <summary>
/// Project access policies on the web (ADR-0020 §4). Any member who can see a project reads its policy; only the workspace's owners and admins save
/// one (<see cref="AccessPolicySaves"/>). Fail-closed: a project or workspace the caller cannot see answers 404.
/// </summary>
public static class AccessPolicyEndpoints
{
    public const string InvalidKey = "invalid_project_key";

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/projects/{projectId:guid}/access-policy", Get);
        g.MapPut("/projects/{projectId:guid}/access-policy", Put);
        g.MapGet("/workspaces/{wsId:guid}/access-policies/by-key", ByKey);
    }

    private static async Task<IResult> Get(Guid projectId, HttpContext http, PodiumDb db)
    {
        var userId = http.User.UserId();
        var grant = await ProjectAccess.OfAsync(db, userId, projectId, http.RequestAborted);
        if (grant is null) return Http.NotFound();
        var admin = await Access.MemberAsync(db, userId, grant.Project.WorkspaceId, Roles.Admin, http.RequestAborted) is not null;
        return Results.Ok(await AccessPolicyStore.PageAsync(db, grant.Project, admin, http.RequestAborted));
    }

    private static Task<IResult> Put(Guid projectId, AccessPolicySave? req, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock) =>
        AccessPolicySaves.SaveAsync(projectId, req, http, db, config, clock);

    private static async Task<IResult> ByKey(Guid wsId, string? projectKey, HttpContext http, PodiumDb db)
    {
        var ct = http.RequestAborted;
        if (await Access.MemberAsync(db, http.User.UserId(), wsId, Roles.Viewer, ct) is null) return Http.NotFound();
        if (!AccessPolicyRules.IsValidKey(projectKey)) return Http.Invalid("projectKey", InvalidKey);
        var enforced = await db.WorkspaceSettings.AsNoTracking().Where(s => s.WorkspaceId == wsId).Select(s => s.EnforceAccessPolicies).FirstOrDefaultAsync(ct);
        if (await AccessPolicyStore.LiveForKeyAsync(db, wsId, projectKey!, ct) is not { } policy) return Http.NotFound();
        var name = await db.Projects.AsNoTracking().Where(p => p.Id == policy.ProjectId).Select(p => p.Name).FirstAsync(ct);
        var document = AccessPolicyStore.Read(policy);
        return Results.Ok(new AccessKeySummary(policy.ProjectId, name, policy.Version, policy.Signature is not null, document.Shell, projectKey!,
            enforced, document));
    }
}
