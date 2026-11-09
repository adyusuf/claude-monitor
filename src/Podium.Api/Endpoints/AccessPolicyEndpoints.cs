using Podium.Api.AccessPolicies;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Projects;
using Podium.Api.Security;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>
/// The access policy a repo key falls under, for the session page: the workspace switch, and the covering policy when there is one (its project,
/// version, whether it is signed and its document). No policy: the default applies when Enforced is on or the machine is strict.
/// </summary>
public sealed record AccessKeySummary(string ProjectKey, bool Enforced, Guid? ProjectId = null, string? ProjectName = null, int? Version = null,
    bool IsSigned = false, AccessPolicyDocument? Document = null);

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
        return Results.Ok(await AccessPolicyStore.ViewAsync(db, grant.Project, admin, http.RequestAborted));
    }

    private static Task<IResult> Put(Guid projectId, AccessPolicySave? req, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock) =>
        AccessPolicySaves.SaveAsync(projectId, req, http, db, config, clock);

    private static async Task<IResult> ByKey(Guid wsId, string? projectKey, HttpContext http, PodiumDb db)
    {
        var ct = http.RequestAborted;
        if (await Access.MemberAsync(db, http.User.UserId(), wsId, Roles.Viewer, ct) is null) return Http.NotFound();
        if (!AccessPolicyRules.IsValidKey(projectKey)) return Http.Invalid("projectKey", InvalidKey);
        var enforced = await db.WorkspaceSettings.AsNoTracking().Where(s => s.WorkspaceId == wsId).Select(s => s.EnforceAccessPolicies).FirstOrDefaultAsync(ct);
        var found = await (from k in db.ProjectAccessPolicyKeys.AsNoTracking()
                           join p in db.ProjectAccessPolicies.AsNoTracking() on k.PolicyId equals p.Id
                           join project in db.Projects.AsNoTracking() on p.ProjectId equals project.Id
                           where k.WorkspaceId == wsId && k.ProjectKey == projectKey && p.WorkspaceId == wsId
                           select new { p, project.Name }).FirstOrDefaultAsync(ct);
        return Results.Ok(found is null
            ? new AccessKeySummary(projectKey!, enforced)
            : new AccessKeySummary(projectKey!, enforced, found.p.ProjectId, found.Name, found.p.Version, found.p.Signature is not null,
                AccessPolicyStore.Read(found.p)));
    }
}
