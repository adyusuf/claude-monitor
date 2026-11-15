using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>The member list of a project is capped, so it can never grow without a bound (never-do list: a list without a limit).</summary>
[Collection(ApiGroup.Name)]
public sealed class ProjectMemberListCapTests(ApiFactory api)
{
    [Fact]
    public async Task The_member_list_stops_at_the_cap_and_lists_by_name()
    {
        var owner = await api.NewClient().SignedUpAsync("cap-owner");
        var project = (await ProjectKit.CreateProjectAsync(owner, "Crowd")).GetProperty("id").GetGuid();
        var now = api.Clock.GetUtcNow();
        await using (var db = api.Db())
        {
            for (var n = 0; n < ProjectEndpoints.MembersMax + 2; n++)
            {
                var user = new User { Email = $"crowd-{n}@gmail.com", EmailNormalized = $"crowd-{n}-{project:N}@gmail.com", DisplayName = $"Person {n:D4}", DisplayNameSearch = $"person {n:D4}", CreatedAt = now, UpdatedAt = now };
                db.Users.Add(user);
                db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = owner.WorkspaceId, UserId = user.Id, Role = "member", JoinedAt = now });
                db.ProjectMembers.Add(new ProjectMember { ProjectId = project, UserId = user.Id, Role = "viewer", AddedBy = owner.Id, AddedAt = now });
            }

            await db.SaveChangesAsync();
        }

        var listed = (await owner.GetJsonAsync($"/api/projects/{project}/members")).EnumerateArray().ToList();

        Assert.Equal(ProjectEndpoints.MembersMax, listed.Count);
        Assert.Equal("Person 0000", listed[0].GetProperty("displayName").GetString());
    }
}
