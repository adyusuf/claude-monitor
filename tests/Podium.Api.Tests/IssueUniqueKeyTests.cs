using System.Net;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>The same code link, run link or project member added at the same moment: one wins and the other gets the duplicate answer, never a 500.</summary>
[Collection(ApiGroup.Name)]
public sealed class IssueUniqueKeyTests(ApiFactory api)
{
    private const int Rounds = 8;

    [Fact]
    public async Task The_same_code_link_added_twice_at_once_is_one_link_and_one_duplicate()
    {
        var t = await IssueKit.TeamAsync(api);

        for (var n = 0; n < Rounds; n++)
        {
            var issue = await IssueKit.IssueAsync(t.Owner, t.Project, $"Code {n}");
            var body = new { kind = "pr", url = "https://git.example.invalid/pull/1" };
            var both = await Task.WhenAll(t.Owner.PostAsync($"/api/issues/{issue}/code-links", body), t.Owner.PostAsync($"/api/issues/{issue}/code-links", body));
            Assert.Single(both, r => r.StatusCode == HttpStatusCode.Created);
            Assert.Equal(("url", "duplicate_link"), await ProjectKit.ProblemAsync(Assert.Single(both, r => r.StatusCode != HttpStatusCode.Created)));
        }
    }
    [Fact]
    public async Task The_same_run_linked_twice_at_once_is_one_link_and_one_duplicate()
    {
        var team = await RemoteKit.TeamAsync(api);
        var project = (await ProjectKit.CreateProjectAsync(team.Admin, "Runs")).GetProperty("id").GetGuid();

        for (var n = 0; n < Rounds; n++)
        {
            var issue = await IssueKit.IssueAsync(team.Admin, project, $"Run {n}");
            var run = (await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), [RemoteKit.Tail, "/var/log/app.log"]))).Id;
            var both = await Task.WhenAll(team.Admin.PostAsync($"/api/issues/{issue}/runs", new { runId = run }), team.Admin.PostAsync($"/api/issues/{issue}/runs", new { runId = run }));
            Assert.Single(both, r => r.StatusCode == HttpStatusCode.NoContent);
            Assert.Equal(("runId", "duplicate_link"), await ProjectKit.ProblemAsync(Assert.Single(both, r => r.StatusCode != HttpStatusCode.NoContent)));
        }
    }
    [Fact]
    public async Task The_same_person_added_to_a_project_twice_at_once_gets_one_row_and_no_error()
    {
        var t = await IssueKit.TeamAsync(api);

        for (var n = 0; n < Rounds; n++)
        {
            var person = await RemoteKit.MemberAsync(api, t.Owner, $"is-member-{n}");
            var both = await Task.WhenAll(
                t.Owner.SendAsync(HttpMethod.Put, $"/api/projects/{t.Project}/members/{person.Id}", new { role = "viewer" }),
                t.Owner.SendAsync(HttpMethod.Put, $"/api/projects/{t.Project}/members/{person.Id}", new { role = "reviewer" }));
            Assert.All(both, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
            var listed = (await t.Owner.GetJsonAsync($"/api/projects/{t.Project}/members")).EnumerateArray();
            Assert.Single(listed, m => m.GetProperty("userId").GetGuid() == person.Id);
        }
    }
}
