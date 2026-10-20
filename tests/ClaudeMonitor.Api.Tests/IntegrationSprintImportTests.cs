using System.Net;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Listing and importing sprints of an imported space (ADR-0015 §4, §8): who may, what is refused and what the answers say.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationSprintImportTests(ApiFactory api)
{
    private static string Url(SprintWorld s, string tail = "sprints") => $"{s.World.Base}/spaces/SPR/{tail}";

    [Fact]
    public async Task The_sprints_of_an_imported_space_are_listed_newest_first_with_their_state_for_any_member()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        var viewer = await RemoteKit.MemberAsync(api, s.World.Admin, "si-viewer", "viewer");
        await JiraKit.ImportSprintsAsync(s.World, "SPR", s.Closed.Id);

        var list = (await viewer.GetJsonAsync(Url(s))).EnumerateArray().ToList();

        Assert.Equal([s.SecondActive.IdText, s.Future.IdText, s.Active.IdText, s.Closed.IdText], list.Select(x => x.GetProperty("id").GetString()));
        Assert.Equal(["active", "planned", "active", "closed"], list.Select(x => x.GetProperty("state").GetString()));
        Assert.Equal(["Sprint four", "Sprint three", "Sprint two", "Sprint one"], list.Select(x => x.GetProperty("name").GetString()));
        Assert.Equal([false, false, false, true], list.Select(x => x.GetProperty("imported").GetBoolean()));
        var one = list[3];
        Assert.Equal(("Warm up", "2026-10-01", "2026-10-14"), (one.GetProperty("goal").GetString(), one.GetProperty("start").GetString(), one.GetProperty("end").GetString()));
        Assert.NotEqual(Guid.Empty, one.GetProperty("sprintId").GetGuid());
    }

    [Fact]
    public async Task Sprints_are_only_for_a_space_that_was_imported_and_only_for_people_of_the_workspace()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        s.World.Site.Space("NEW", "Not imported");
        var stranger = await api.NewClient().SignedUpAsync("si-stranger");
        var otherWorkspace = await JiraKit.ConnectedAsync(api);

        Assert.Equal(HttpStatusCode.NotFound, (await s.World.Admin.Http.GetAsync($"{s.World.Base}/spaces/NEW/sprints")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.World.Admin.PostAsync($"{s.World.Base}/spaces/NEW/sprints/import", new { sprintIds = new[] { "1" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.World.Admin.Http.GetAsync($"{s.World.Base}/spaces/spr/sprints")).StatusCode); // keys are exact
        foreach (var user in new[] { stranger, otherWorkspace.Admin })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync(Url(s))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await user.PostAsync(Url(s, "sprints/import"), new { sprintIds = new[] { s.Closed.IdText } })).StatusCode);
        }

        Assert.Empty(await JiraKit.SprintsAsync(s.World, s.Project));
    }

    [Fact]
    public async Task Importing_a_sprint_needs_the_right_to_manage_the_project()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        var admin = await RemoteKit.MemberAsync(api, s.World.Admin, "si-admin", "admin");
        var manager = await RemoteKit.MemberAsync(api, s.World.Admin, "si-manager");
        var contributor = await RemoteKit.MemberAsync(api, s.World.Admin, "si-contributor");
        var reviewer = await RemoteKit.MemberAsync(api, s.World.Admin, "si-reviewer");
        var bystander = await RemoteKit.MemberAsync(api, s.World.Admin, "si-bystander");
        foreach (var (user, role) in new[] { (manager, "manager"), (contributor, "contributor"), (reviewer, "reviewer") })
        {
            (await s.World.Admin.SendAsync(HttpMethod.Put, $"/api/projects/{s.Project}/members/{user.Id}", new { role })).EnsureSuccessStatusCode();
        }

        async Task<HttpStatusCode> Import(TestUser user, FakeSprint sprint) => (await user.PostAsync(Url(s, "sprints/import"), new { sprintIds = new[] { sprint.IdText } })).StatusCode;

        Assert.Equal(HttpStatusCode.Forbidden, await Import(contributor, s.Closed));
        Assert.Equal(HttpStatusCode.Forbidden, await Import(reviewer, s.Closed));
        Assert.Equal(HttpStatusCode.Forbidden, await Import(bystander, s.Closed)); // a member with no role in the project
        Assert.Empty(await JiraKit.SprintsAsync(s.World, s.Project));
        Assert.Equal(HttpStatusCode.OK, await Import(manager, s.Closed));
        Assert.Equal(HttpStatusCode.OK, await Import(admin, s.Active));
        Assert.Equal(HttpStatusCode.OK, await Import(s.World.Admin, s.Future));
        Assert.Equal(3, (await JiraKit.SprintsAsync(s.World, s.Project)).Count);
    }

    [Fact]
    public async Task The_list_of_sprint_ids_must_have_between_one_and_twenty_numeric_ids()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        async Task<(string, string)> Refused(params string[] ids) => await ProjectKit.ProblemAsync(await s.World.Admin.PostAsync(Url(s, "sprints/import"), new { sprintIds = ids }));

        Assert.Equal(("sprintIds", "invalid_list"), await Refused());
        Assert.Equal(("sprintIds", "invalid_list"), await Refused("abc", "12x", "", "-1"));
        Assert.Equal(("sprintIds", "invalid_list"), await Refused(new string('1', 21)));
        Assert.Equal(("sprintIds", "invalid_list"), await Refused([.. Enumerable.Range(1, 21).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))]));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.World.Admin.PostAsync(Url(s, "sprints/import"), new { sprintIds = (string[]?)null })).StatusCode);
        Assert.Empty(await JiraKit.SprintsAsync(s.World, s.Project));
    }

    [Fact]
    public async Task A_sprint_the_space_does_not_have_is_reported_while_the_others_import()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        var elsewhere = s.World.Site.Space("ELS");
        var foreign = s.World.Site.Sprint(elsewhere, "Another space's", "future");

        var answer = (await JiraKit.ImportSprintsAsync(s.World, "SPR", s.Closed.Id, foreign.Id, 999999999)).EnumerateArray()
            .Select(r => (r.GetProperty("sprintId").GetString(), r.GetProperty("outcome").GetString())).ToList();

        Assert.Equal([(s.Closed.IdText, "ok"), (foreign.IdText, "space_not_found"), ("999999999", "space_not_found")], answer);
        Assert.Equal(["Sprint one"], (await JiraKit.SprintsAsync(s.World, s.Project)).Select(x => x.Name));
    }

    [Fact]
    public async Task A_failing_read_reports_every_sprint_with_its_code_and_writes_nothing()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        api.Jira.Fail(s.World.Site, $"/sprint/{s.Active.IdText}/issue", HttpStatusCode.InternalServerError);

        var answer = await JiraKit.ImportSprintsAsync(s.World, "SPR", s.Closed.Id, s.Active.Id);

        Assert.Equal(["tracker_unavailable", "tracker_unavailable"], answer.EnumerateArray().Select(r => r.GetProperty("outcome").GetString()));
        Assert.Empty(await JiraKit.SprintsAsync(s.World, s.Project));
        await using var db = api.Db();
        var failed = await db.IntegrationSyncRuns.AsNoTracking().SingleAsync(r => r.ConnectionId == s.World.ConnectionId && r.Status == SyncStatuses.Failed);
        Assert.Equal((SyncKinds.Import, 0, 0), (failed.Kind, failed.Created, failed.Updated));
        Assert.NotNull(failed.EndedAt);
    }

    [Fact]
    public async Task A_connector_who_must_sign_in_again_imports_no_sprint()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        await using (var db = api.Db())
        {
            await db.IntegrationConnections.Where(c => c.Id == s.World.ConnectionId).ExecuteUpdateAsync(c => c.SetProperty(x => x.Status, ConnectionStatuses.Reauthorize));
        }

        var answer = await JiraKit.ImportSprintsAsync(s.World, "SPR", s.Closed.Id);
        var listing = await s.World.Admin.Http.GetAsync(Url(s));

        Assert.Equal("reauthorize", answer[0].GetProperty("outcome").GetString());
        await RemoteKit.AssertProblemAsync(listing, HttpStatusCode.Conflict, "reauthorize");
        Assert.Empty(await JiraKit.SprintsAsync(s.World, s.Project));
    }
}
