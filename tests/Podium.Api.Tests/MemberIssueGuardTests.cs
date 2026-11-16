using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// F-102 (B), the member part: removing a member, or lowering them below Member, is refused with 409 member_has_issues (count and archivedCount in
/// the body) while issues of the workspace are still assigned to them; archived ones count, a restore would bring them back.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class MemberIssueGuardTests(ApiFactory api)
{
    private static string MemberUrl(TestUser owner, TestUser member) => $"/api/workspaces/{owner.WorkspaceId}/members/{member.Id}";

    private static async Task<Guid> AssignedAsync(IssueTeam t, string title, bool archived = false)
    {
        var id = await IssueKit.IssueAsync(t.Owner, t.Project, title, extra: new { assigneeUserId = t.Contributor.Id });
        if (archived) (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{id}")).EnsureSuccessStatusCode();
        return id;
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, int count, int archivedCount)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("member_has_issues", count, archivedCount),
            (body.GetProperty("title").GetString(), body.GetProperty("count").GetInt32(), body.GetProperty("archivedCount").GetInt32()));
    }

    private async Task<(string Role, bool Removed)> StoredAsync(IssueTeam t)
    {
        await using var db = api.Db();
        var m = await db.WorkspaceMembers.AsNoTracking().SingleAsync(x => x.WorkspaceId == t.Owner.WorkspaceId && x.UserId == t.Contributor.Id);
        return (m.Role, m.RemovedAt is not null);
    }

    [Fact]
    public async Task Removing_a_member_with_assigned_issues_is_refused_with_both_counts_and_changes_nothing()
    {
        var t = await IssueKit.TeamAsync(api);
        await AssignedAsync(t, "Open one");
        await AssignedAsync(t, "Open two");
        await AssignedAsync(t, "Archived one", archived: true);

        await AssertRefusedAsync(await t.Owner.SendAsync(HttpMethod.Delete, MemberUrl(t.Owner, t.Contributor)), 2, 1);

        Assert.Equal(("member", false), await StoredAsync(t));
    }

    [Fact]
    public async Task Archived_issues_alone_still_refuse_the_removal()
    {
        var t = await IssueKit.TeamAsync(api);
        await AssignedAsync(t, "Archived only", archived: true);

        await AssertRefusedAsync(await t.Owner.SendAsync(HttpMethod.Delete, MemberUrl(t.Owner, t.Contributor)), 0, 1);
    }

    [Fact]
    public async Task A_member_can_not_leave_while_issues_are_assigned_to_them_either()
    {
        var t = await IssueKit.TeamAsync(api);
        await AssignedAsync(t, "Mine");

        await AssertRefusedAsync(await t.Contributor.SendAsync(HttpMethod.Delete, MemberUrl(t.Owner, t.Contributor)), 1, 0);
    }

    [Fact]
    public async Task A_member_with_no_issues_or_only_unassigned_ones_around_is_removed()
    {
        var t = await IssueKit.TeamAsync(api);
        await IssueKit.IssueAsync(t.Owner, t.Project, "Nobody's");

        var response = await t.Owner.SendAsync(HttpMethod.Delete, MemberUrl(t.Owner, t.Contributor));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(("member", true), await StoredAsync(t));
    }

    [Fact]
    public async Task Another_persons_issues_do_not_hold_a_member_back()
    {
        var t = await IssueKit.TeamAsync(api);
        await IssueKit.IssueAsync(t.Owner, t.Project, "Reviewer's", extra: new { assigneeUserId = t.Reviewer.Id });

        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Delete, MemberUrl(t.Owner, t.Contributor))).StatusCode);
    }

    [Fact]
    public async Task Lowering_a_member_with_assigned_issues_to_viewer_is_refused_and_the_role_is_kept()
    {
        var t = await IssueKit.TeamAsync(api);
        await AssignedAsync(t, "Open");
        await AssignedAsync(t, "Archived", archived: true);

        await AssertRefusedAsync(await t.Owner.SendAsync(HttpMethod.Patch, MemberUrl(t.Owner, t.Contributor), new { role = "viewer" }), 1, 1);

        Assert.Equal(("member", false), await StoredAsync(t));
    }

    [Fact]
    public async Task Lowering_a_member_with_no_assigned_issues_to_viewer_is_allowed()
    {
        var t = await IssueKit.TeamAsync(api);
        await IssueKit.IssueAsync(t.Owner, t.Project, "Nobody's");

        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Patch, MemberUrl(t.Owner, t.Contributor), new { role = "viewer" })).StatusCode);
        Assert.Equal(("viewer", false), await StoredAsync(t));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("member")]
    public async Task Raising_or_keeping_the_role_is_not_held_back_by_assigned_issues(string role)
    {
        var t = await IssueKit.TeamAsync(api);
        await AssignedAsync(t, "Open");

        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Patch, MemberUrl(t.Owner, t.Contributor), new { role })).StatusCode);
        Assert.Equal((role, false), await StoredAsync(t));
    }

    [Fact]
    public async Task Lowering_an_admin_to_member_is_not_held_back_by_assigned_issues()
    {
        var t = await IssueKit.TeamAsync(api);
        await AssignedAsync(t, "Open");
        (await t.Owner.SendAsync(HttpMethod.Patch, MemberUrl(t.Owner, t.Contributor), new { role = "admin" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Patch, MemberUrl(t.Owner, t.Contributor), new { role = "member" })).StatusCode);
    }
}
