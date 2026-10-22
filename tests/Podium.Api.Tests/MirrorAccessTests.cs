using System.Net;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>Writing to the tool stays fail-closed (global #6): the same rights as for any issue, checked before anything is sent.</summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorAccessTests(ApiFactory api)
{
    private static (string Name, Func<TestUser, Task<HttpResponseMessage>> Send)[] Calls(SprintSet s, Guid issue, Guid sprint, Guid planned)
    {
        var project = s.W.Project;
        return
        [
            ("edit issue", u => u.SendAsync(HttpMethod.Put, $"/api/issues/{issue}", new { title = "Changed" })),
            ("quick status", u => u.SendAsync(HttpMethod.Put, $"/api/issues/{issue}/status", new { status = "done" })),
            ("create issue", u => u.PostAsync($"/api/projects/{project}/issues", new { title = "New" })),
            ("rank issue", u => u.SendAsync(HttpMethod.Put, $"/api/issues/{issue}/position", new { afterIssueId = (Guid?)null })),
            ("archive issue", u => u.SendAsync(HttpMethod.Delete, $"/api/issues/{issue}")),
            ("restore issue", u => u.PostAsync($"/api/issues/{issue}/restore")),
            ("create sprint", u => u.PostAsync($"/api/projects/{project}/sprints", new { name = "New" })),
            ("edit sprint", u => u.SendAsync(HttpMethod.Put, $"/api/sprints/{planned}", new { name = "Changed" })),
            ("start sprint", u => u.PostAsync($"/api/sprints/{planned}/start")),
            ("close sprint", u => u.PostAsync($"/api/sprints/{sprint}/close", new { moveUnfinishedTo = (Guid?)null })),
            ("delete sprint", u => u.SendAsync(HttpMethod.Delete, $"/api/sprints/{planned}")),
            ("add issues", u => u.PostAsync($"/api/sprints/{planned}/issues", new { issueIds = new[] { issue } })),
            ("remove issue", u => u.SendAsync(HttpMethod.Delete, $"/api/sprints/{sprint}/issues/{issue}")),
            ("edit project", u => u.SendAsync(HttpMethod.Put, $"/api/projects/{project}", new { name = "Changed" })),
        ];
    }

    [Fact]
    public async Task A_viewer_a_member_without_a_project_role_and_a_stranger_cannot_write_and_nothing_reaches_jira()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var viewer = await RemoteKit.MemberAsync(api, s.W.World.Admin, "wr-viewer", "viewer");
        var plain = await RemoteKit.MemberAsync(api, s.W.World.Admin, "wr-plain");
        var stranger = await api.NewClient().SignedUpAsync("wr-stranger");
        var issue = await s.W.IdAsync("WRT-6");
        var calls = Calls(s, issue, await s.IdAsync("Now"), await s.IdAsync("Plan A"));
        var before = s.W.Jira.Calls(s.W.Site).Count;

        foreach (var (name, send) in calls)
        {
            Assert.True((await send(viewer)).StatusCode == HttpStatusCode.Forbidden, $"a viewer: {name}");
            Assert.True((await send(plain)).StatusCode == HttpStatusCode.Forbidden, $"a member without a project role: {name}");
            Assert.True((await send(stranger)).StatusCode == HttpStatusCode.NotFound, $"a stranger: {name}");
        }

        Assert.Equal(before, s.W.Jira.Calls(s.W.Site).Count);
        var row = await s.W.RowAsync("WRT-6");
        Assert.Equal(("Waiting in now", "ready", null), (row.Title, row.Status, row.ArchivedAt));
    }

    [Fact]
    public async Task A_person_who_may_edit_still_writes_with_their_own_account_and_never_with_somebody_elses()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var other = await RemoteKit.MemberAsync(api, s.W.World.Admin, "wr-other");
        (await s.W.World.Admin.SendAsync(HttpMethod.Put, $"/api/projects/{s.W.Project}/members/{other.Id}", new { role = "contributor" })).EnsureSuccessStatusCode();
        var otherAccount = await JiraWriteKit.LinkAccountAsync(other, s.W.World);

        (await JiraWriteKit.EditAsync(other, await s.W.IdAsync("WRT-2"), new { title = "By the other" })).EnsureSuccessStatusCode();
        (await JiraWriteKit.EditAsync(s.W.Member, await s.W.IdAsync("WRT-3"), new { title = "By the member" })).EnsureSuccessStatusCode();
        (await JiraWriteKit.EditAsync(s.W.World.Admin, await s.W.IdAsync("WRT-4"), new { title = "By the admin" })).EnsureSuccessStatusCode();

        Assert.Equal([otherAccount.Id], s.W.Jira.Callers(s.W.Site, HttpMethod.Put, "/rest/api/3/issue/WRT-2"));
        Assert.Equal([s.W.MemberAccount!.Id], s.W.Jira.Callers(s.W.Site, HttpMethod.Put, "/rest/api/3/issue/WRT-3"));
        Assert.Equal([s.W.World.Account.Id], s.W.Jira.Callers(s.W.Site, HttpMethod.Put, "/rest/api/3/issue/WRT-4"));
    }
}
