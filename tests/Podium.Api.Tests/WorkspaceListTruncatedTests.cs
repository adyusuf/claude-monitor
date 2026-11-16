using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// F-060: the members and invitations lists asked without limit or cursor are cut at <see cref="WorkspaceMemberList.ListMax"/>; they say so with
/// <c>X-Truncated: true</c> (as the Jira lists do, F-077) and say nothing when the list fits exactly or a page was asked for.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class WorkspaceListTruncatedTests(ApiFactory api)
{
    private static bool Truncated(HttpResponseMessage response) =>
        response.Headers.TryGetValues(IntegrationImportEndpoints.TruncatedHeader, out var values) && values.Single() == "true";

    private async Task AddMembersAsync(TestUser owner, int count)
    {
        var now = api.Clock.GetUtcNow();
        await using var db = api.Db();
        for (var n = 0; n < count; n++)
        {
            var key = $"trunc-{n}-{Guid.NewGuid():N}@gmail.com";
            var user = new User { Email = key, EmailNormalized = key, DisplayName = $"Person {n:D4}", DisplayNameSearch = $"person {n:D4}", CreatedAt = now, UpdatedAt = now };
            db.Users.Add(user);
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = owner.WorkspaceId, UserId = user.Id, Role = Roles.Member, JoinedAt = now });
        }

        await db.SaveChangesAsync();
    }

    private async Task AddInvitationsAsync(TestUser owner, int count)
    {
        var now = api.Clock.GetUtcNow();
        await using var db = api.Db();
        for (var n = 0; n < count; n++)
        {
            var key = $"trunc-inv-{n}-{Guid.NewGuid():N}@gmail.com";
            db.WorkspaceInvitations.Add(new WorkspaceInvitation
            {
                WorkspaceId = owner.WorkspaceId, EmailNormalized = key, Email = key, Role = Roles.Member, TokenHash = Secrets.Hash(Secrets.NewToken()),
                InvitedBy = owner.Id, CreatedAt = now, ExpiresAt = now.AddDays(7),
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task<int> CountAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength();
    }

    [Theory]
    [InlineData("members")]
    [InlineData("invitations")]
    public async Task A_list_cut_at_the_cap_says_so_and_one_that_fits_or_is_paged_does_not(string list)
    {
        var over = await api.NewClient().SignedUpAsync("trunc-over-" + list);
        var exact = await api.NewClient().SignedUpAsync("trunc-exact-" + list);
        // The owner is a member already: 499 more fill the member list to exactly the cap, 500 more make it one over.
        var (overRows, exactRows) = list == "members" ? (WorkspaceMemberList.ListMax, WorkspaceMemberList.ListMax - 1) : (WorkspaceMemberList.ListMax + 1, WorkspaceMemberList.ListMax);
        foreach (var (user, rows) in new[] { (over, overRows), (exact, exactRows) })
        {
            if (list == "members") await AddMembersAsync(user, rows);
            else await AddInvitationsAsync(user, rows);
        }

        var cut = await over.Http.GetAsync($"/api/workspaces/{over.WorkspaceId}/{list}");
        var whole = await exact.Http.GetAsync($"/api/workspaces/{exact.WorkspaceId}/{list}");
        var paged = await over.Http.GetAsync($"/api/workspaces/{over.WorkspaceId}/{list}?limit=10");

        Assert.Equal((WorkspaceMemberList.ListMax, true), (await CountAsync(cut), Truncated(cut)));
        Assert.Equal((WorkspaceMemberList.ListMax, false), (await CountAsync(whole), Truncated(whole)));
        Assert.False(Truncated(paged));
    }
}
