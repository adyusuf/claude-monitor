using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>A person removed from the workspace keeps no project role: not in the project's member list, not after an invitation back.</summary>
[Collection(ApiGroup.Name)]
public sealed class MemberRemovalProjectTests(ApiFactory api)
{
    [Fact]
    public async Task Removing_a_member_removes_their_project_roles()
    {
        var owner = await api.NewClient().SignedUpAsync("pr-owner");
        var manager = await RemoteKit.MemberAsync(api, owner, "pr-manager");
        var id = (await ProjectKit.CreateProjectAsync(owner, "Kept")).GetProperty("id").GetGuid();
        (await owner.SendAsync(HttpMethod.Put, $"/api/projects/{id}/members/{manager.Id}", new { role = ProjectRoles.Manager })).EnsureSuccessStatusCode();
        (await owner.SendAsync(HttpMethod.Delete, $"/api/workspaces/{owner.WorkspaceId}/members/{manager.Id}")).EnsureSuccessStatusCode();

        Assert.Empty((await owner.GetJsonAsync($"/api/projects/{id}/members")).EnumerateArray());

        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = manager.Email, role = Roles.Member })).EnsureSuccessStatusCode();
        (await manager.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(manager.Email) })).EnsureSuccessStatusCode();

        Assert.False((await manager.GetJsonAsync($"/api/projects/{id}")).GetProperty("access").GetProperty("canEdit").GetBoolean());
        Assert.Empty((await owner.GetJsonAsync($"/api/projects/{id}/members")).EnumerateArray());
    }

    [Fact]
    public async Task The_project_member_list_leaves_out_a_row_left_behind_by_a_removed_member()
    {
        var owner = await api.NewClient().SignedUpAsync("pr-list-owner");
        var leaver = await RemoteKit.MemberAsync(api, owner, "pr-list-leaver");
        var id = (await ProjectKit.CreateProjectAsync(owner, "Leftover")).GetProperty("id").GetGuid();
        (await owner.SendAsync(HttpMethod.Delete, $"/api/workspaces/{owner.WorkspaceId}/members/{leaver.Id}")).EnsureSuccessStatusCode();
        // A row written before removals cleaned project roles up.
        await using (var db = api.Db())
        {
            db.ProjectMembers.Add(new ProjectMember
            {
                ProjectId = id, UserId = leaver.Id, Role = ProjectRoles.Contributor, AddedBy = owner.Id, AddedAt = api.Clock.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }

        Assert.Empty((await owner.GetJsonAsync($"/api/projects/{id}/members")).EnumerateArray());
    }
}
