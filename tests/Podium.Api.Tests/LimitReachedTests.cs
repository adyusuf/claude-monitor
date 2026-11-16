using System.Net;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>The caps on what a project, an application and an issue hold: the call after the last free place answers <c>limit_reached</c> and stores nothing.</summary>
[Collection(ApiGroup.Name)]
public sealed class LimitReachedTests(ApiFactory api)
{
    // The caps are the contract (ADR-0011), written out here so that changing one fails a test on purpose.
    private const int ApplicationsMax = 100;
    private const int ComponentsMax = 200;
    private const int ReleasesMax = 200;
    private const int TestCasesMax = 100;
    private const int CodeLinksMax = 50;
    private const int IssueLinksMax = 50;
    private const int RunLinksMax = 50;

    private static async Task FillAsync(int count, Func<int, Task<HttpResponseMessage>> add)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await add(i)).StatusCode);
        }
    }

    private static async Task<int> CountAsync(TestUser user, string url) => (await user.GetJsonAsync(url)).GetArrayLength();

    [Fact]
    public async Task The_101st_active_application_of_a_project_is_refused()
    {
        var t = await IssueKit.TeamAsync(api); // the team's project already has one
        await FillAsync(ApplicationsMax - 1, i => t.Owner.PostAsync($"/api/projects/{t.Project}/applications", ProjectKit.Application("App " + i)));

        var response = await t.Owner.PostAsync($"/api/projects/{t.Project}/applications", ProjectKit.Application("One too many"));

        Assert.Equal(("name", "limit_reached"), await ProjectKit.ProblemAsync(response));
        Assert.Equal(ApplicationsMax, (await t.Owner.GetJsonAsync($"/api/projects/{t.Project}/applications?limit=100")).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task The_201st_active_component_of_an_application_is_refused()
    {
        var t = await IssueKit.TeamAsync(api); // the team's application already has one
        await FillAsync(ComponentsMax - 1, i => t.Owner.PostAsync($"/api/applications/{t.Application}/components", new { name = "Part " + i }));

        var response = await t.Owner.PostAsync($"/api/applications/{t.Application}/components", new { name = "One too many" });

        Assert.Equal(("name", "limit_reached"), await ProjectKit.ProblemAsync(response));
        Assert.Equal(ComponentsMax, await CountAsync(t.Owner, $"/api/applications/{t.Application}/components"));
    }

    [Fact]
    public async Task The_201st_planned_release_of_an_application_is_refused()
    {
        var t = await IssueKit.TeamAsync(api); // the team's application already has one
        await FillAsync(ReleasesMax - 1, i => t.Owner.PostAsync($"/api/applications/{t.Application}/releases", new { name = "r" + i }));

        var response = await t.Owner.PostAsync($"/api/applications/{t.Application}/releases", new { name = "One too many" });

        Assert.Equal(("name", "limit_reached"), await ProjectKit.ProblemAsync(response));
        Assert.Equal(ReleasesMax, await CountAsync(t.Owner, $"/api/applications/{t.Application}/releases"));
    }

    [Fact]
    public async Task The_101st_test_case_of_an_issue_is_refused()
    {
        var t = await IssueKit.TeamAsync(api);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Checked");
        await FillAsync(TestCasesMax, i => t.Owner.PostAsync($"/api/issues/{issue}/test-cases", new { title = "Case " + i }));

        var response = await t.Owner.PostAsync($"/api/issues/{issue}/test-cases", new { title = "One too many" });

        Assert.Equal(("title", "limit_reached"), await ProjectKit.ProblemAsync(response));
        Assert.Equal(TestCasesMax, await CountAsync(t.Owner, $"/api/issues/{issue}/test-cases"));
    }

    [Fact]
    public async Task The_51st_code_link_of_an_issue_is_refused()
    {
        var t = await IssueKit.TeamAsync(api);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Shipped");
        await FillAsync(CodeLinksMax, i => t.Owner.PostAsync($"/api/issues/{issue}/code-links", new { kind = "pr", url = $"https://git.example.invalid/team/shop/pull/{i + 1}" }));

        var response = await t.Owner.PostAsync($"/api/issues/{issue}/code-links", new { kind = "pr", url = "https://git.example.invalid/team/shop/pull/9999" });

        Assert.Equal(("url", "limit_reached"), await ProjectKit.ProblemAsync(response));
        Assert.Equal(CodeLinksMax, await CountAsync(t.Owner, $"/api/issues/{issue}/code-links"));
    }

    [Fact]
    public async Task The_51st_issue_link_of_an_issue_is_refused()
    {
        var t = await IssueKit.TeamAsync(api);
        var hub = await IssueKit.IssueAsync(t.Owner, t.Project, "Hub");
        var others = new List<Guid>();
        for (var i = 0; i <= IssueLinksMax; i++) others.Add(await IssueKit.IssueAsync(t.Owner, t.Project, "Other " + i));
        foreach (var other in others.Take(IssueLinksMax))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.PostAsync($"/api/issues/{hub}/links", new { otherIssueId = other, kind = "blocks" })).StatusCode);
        }

        var response = await t.Owner.PostAsync($"/api/issues/{hub}/links", new { otherIssueId = others[^1], kind = "blocks" });

        Assert.Equal(("otherIssueId", "limit_reached"), await ProjectKit.ProblemAsync(response));
        Assert.Equal(IssueLinksMax, (await IssueKit.ViewAsync(t.Owner, hub)).GetProperty("blocks").GetArrayLength());
    }

    [Fact]
    public async Task The_51st_run_linked_to_an_issue_is_refused()
    {
        var team = await RemoteKit.TeamAsync(api);
        await RemoteKit.ActiveGrantAsync(team);
        var project = (await ProjectKit.CreateProjectAsync(team.Admin, "Runs")).GetProperty("id").GetGuid();
        var issue = await IssueKit.IssueAsync(team.Admin, project, "Many builds", "bug");
        var runs = new List<Guid>();
        for (var i = 0; i <= RunLinksMax; i++)
        {
            if (i > 0 && i % 20 == 0) api.Clock.Advance(TimeSpan.FromSeconds(61)); // the requester may start 20 runs a minute
            runs.Add((await RemoteKit.CreateAsync(team.Requester, RemoteKit.GrantRun(team.TargetId))).Id);
        }

        foreach (var run in runs.Take(RunLinksMax))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await team.Admin.PostAsync($"/api/issues/{issue}/runs", new { runId = run })).StatusCode);
        }

        var response = await team.Admin.PostAsync($"/api/issues/{issue}/runs", new { runId = runs[^1] });

        Assert.Equal(("runId", "limit_reached"), await ProjectKit.ProblemAsync(response));
        Assert.Equal(RunLinksMax, await CountAsync(team.Admin, $"/api/issues/{issue}/runs"));
    }
}
