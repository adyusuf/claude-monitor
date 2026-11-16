using Podium.Api.Data;
using Podium.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>The workspace's member list (GET /workspaces/{id}/members).</summary>
public static class WorkspaceMemberList
{
    /// <summary>
    /// Any member reads it. Admins and owners see each member's e-mail address; viewers and members get null in its place (F-225): the
    /// page and the pickers need names and roles, not other people's addresses.
    /// </summary>
    public static async Task<IResult> ListAsync(Guid id, HttpContext http, PodiumDb db)
    {
        var reader = await Access.MemberAsync(db, http.User.UserId(), id, Roles.Viewer, http.RequestAborted);
        if (reader is null) return Http.NotFound();
        var seesEmail = Roles.Rank(reader.Role) >= Roles.Rank(Roles.Admin);
        var members = await (from m in db.WorkspaceMembers.AsNoTracking()
                             join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                             where m.WorkspaceId == id && m.RemovedAt == null
                             orderby u.DisplayName
                             select new MemberResponse(u.Id, u.DisplayName, u.Email, m.Role, m.JoinedAt)).ToListAsync(http.RequestAborted);
        return Results.Ok(seesEmail ? members : members.Select(m => m with { Email = null }).ToList());
    }
}
