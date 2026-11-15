using System.Net;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>Fail-closed on every sprint route (global #6): no session is 401, a stranger gets 404, a viewer only reads; nothing changes.</summary>
[Collection(ApiGroup.Name)]
public sealed class SprintAccessTests(ApiFactory api)
{
    public static TheoryData<string, string, bool> Routes => new()
    {
        { "GET", "/api/projects/{project}/sprints", false },
        { "POST", "/api/projects/{project}/sprints", true },
        { "GET", "/api/sprints/{sprint}", false },
        { "PUT", "/api/sprints/{sprint}", true },
        { "DELETE", "/api/sprints/{sprint}", true },
        { "POST", "/api/sprints/{sprint}/start", true },
        { "POST", "/api/sprints/{sprint}/close", true },
        { "POST", "/api/sprints/{sprint}/issues", true },
        { "DELETE", "/api/sprints/{sprint}/issues/{issue}", true },
        { "PUT", "/api/issues/{issue}/status", true },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Every_sprint_route_refuses_no_session_and_strangers_and_lets_a_viewer_only_read(string method, string template, bool write)
    {
        var t = await IssueKit.TeamAsync(api);
        var stranger = await api.NewClient().SignedUpAsync("sa-stranger");
        var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Guarded", "story", new { status = "in_progress" });
        (await SprintKit.AddAsync(t.Owner, sprint, issue)).EnsureSuccessStatusCode();
        var url = template.Replace("{project}", t.Project.ToString(), StringComparison.Ordinal).Replace("{sprint}", sprint.ToString(), StringComparison.Ordinal)
            .Replace("{issue}", issue.ToString(), StringComparison.Ordinal);
        var body = new { goal = "Intruder", startDate = "2026-11-02", endDate = "2026-11-13", issueIds = new[] { issue }, status = "done" };
        Task<HttpResponseMessage> Send(TestUser who) => who.SendAsync(new HttpMethod(method), url, method == "GET" ? null : body);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(api.NewClient())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(stranger)).StatusCode);
        Assert.Equal(write ? HttpStatusCode.Forbidden : HttpStatusCode.OK, (await Send(t.Viewer)).StatusCode);

        var s = (await SprintKit.ViewAsync(t.Owner, sprint)).GetProperty("sprint");
        Assert.Equal(("planned", "Ship it", 1), (s.GetProperty("status").GetString(), s.GetProperty("goal").GetString(), s.GetProperty("issueCount").GetInt32()));
        Assert.Equal((sprint, "in_progress"), (await SprintKit.IssueSprintAsync(t.Owner, issue), await SprintKit.IssueStatusAsync(t.Owner, issue)));
        Assert.Equal(1, (await t.Owner.GetJsonAsync($"/api/projects/{t.Project}/sprints")).GetProperty("items").GetArrayLength());
    }
}
