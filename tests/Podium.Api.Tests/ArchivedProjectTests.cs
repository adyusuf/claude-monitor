using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>An archived project is read-only until it is restored: every change below it is refused, reading still works.</summary>
[Collection(ApiGroup.Name)]
public sealed class ArchivedProjectTests(ApiFactory api)
{
    private static async Task<Guid> IdAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task No_issue_application_component_release_or_member_of_an_archived_project_changes()
    {
        var t = await IssueKit.TeamAsync(api);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Live");
        var other = await IssueKit.IssueAsync(t.Owner, t.Project, "Other");
        var shelved = await IssueKit.IssueAsync(t.Owner, t.Project, "Shelved");
        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{shelved}")).EnsureSuccessStatusCode();
        (await t.Owner.PostAsync($"/api/issues/{issue}/links", new { kind = "blocks", otherIssueId = other })).EnsureSuccessStatusCode();
        var link = (await IssueKit.ViewAsync(t.Owner, issue)).GetProperty("blocks")[0].GetProperty("linkId").GetGuid();
        var testCase = await IdAsync(await t.Owner.PostAsync($"/api/issues/{issue}/test-cases", new { title = "Pay" }));
        var codeLink = await IdAsync(await t.Owner.PostAsync($"/api/issues/{issue}/code-links", new { kind = "pr", url = "https://git.example.invalid/team/shop/pull/1" }));
        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/projects/{t.Project}")).EnsureSuccessStatusCode();

        var calls = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Put, $"/api/issues/{issue}", new { title = "Changed" }),
            (HttpMethod.Put, $"/api/issues/{issue}/position", new { afterIssueId = other }),
            (HttpMethod.Put, $"/api/issues/{issue}/status", new { status = "done" }),
            (HttpMethod.Delete, $"/api/issues/{issue}", null),
            (HttpMethod.Post, $"/api/issues/{shelved}/restore", null),
            (HttpMethod.Post, $"/api/issues/{other}/links", new { kind = "blocks", otherIssueId = shelved }),
            (HttpMethod.Delete, $"/api/issue-links/{link}", null),
            (HttpMethod.Post, $"/api/issues/{issue}/test-cases", new { title = "More" }),
            (HttpMethod.Put, $"/api/test-cases/{testCase}", new { title = "Changed" }),
            (HttpMethod.Post, $"/api/test-cases/{testCase}/result", new { result = "passed" }),
            (HttpMethod.Delete, $"/api/test-cases/{testCase}", null),
            (HttpMethod.Post, $"/api/issues/{issue}/code-links", new { kind = "pr", url = "https://git.example.invalid/team/shop/pull/2" }),
            (HttpMethod.Delete, $"/api/code-links/{codeLink}", null),
            (HttpMethod.Post, $"/api/issues/{issue}/runs", new { runId = Guid.NewGuid() }),
            (HttpMethod.Delete, $"/api/issues/{issue}/runs/{Guid.NewGuid()}", null),
            (HttpMethod.Put, $"/api/applications/{t.Application}", ProjectKit.Application("Changed")),
            (HttpMethod.Delete, $"/api/applications/{t.Application}", null),
            (HttpMethod.Post, $"/api/applications/{t.Application}/restore", null),
            (HttpMethod.Post, $"/api/applications/{t.Application}/components", new { name = "Late" }),
            (HttpMethod.Put, $"/api/components/{t.Component}", new { name = "Changed" }),
            (HttpMethod.Delete, $"/api/components/{t.Component}", null),
            (HttpMethod.Post, $"/api/applications/{t.Application}/releases", new { name = "Late" }),
            (HttpMethod.Put, $"/api/releases/{t.Release}", new { name = "Changed" }),
            (HttpMethod.Delete, $"/api/releases/{t.Release}", null),
            (HttpMethod.Put, $"/api/projects/{t.Project}/members/{t.Contributor.Id}", new { role = "manager" }),
            (HttpMethod.Delete, $"/api/projects/{t.Project}/members/{t.Reviewer.Id}", null),
        };
        foreach (var (method, url, body) in calls)
        {
            var response = method == HttpMethod.Post ? await t.Owner.PostAsync(url, body) : await t.Owner.SendAsync(method, url, body);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{method} {url} answered {(int)response.StatusCode}");
            Assert.Equal(("status", "archived"), await ProjectKit.ProblemAsync(response));
        }

        var read = await IssueKit.ViewAsync(t.Reviewer, issue);
        Assert.Equal(("Live", 1), (read.GetProperty("issue").GetProperty("title").GetString(), read.GetProperty("blocks").GetArrayLength()));
        Assert.Single((await t.Reviewer.GetJsonAsync($"/api/issues/{issue}/test-cases")).EnumerateArray());
        Assert.Equal(["Cart"], (await t.Reviewer.GetJsonAsync($"/api/applications/{t.Application}/components")).EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.Equal("active", (await t.Reviewer.GetJsonAsync($"/api/applications/{t.Application}")).GetProperty("status").GetString());

        (await t.Owner.PostAsync($"/api/projects/{t.Project}/restore")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{issue}", new { title = "Changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Put, $"/api/components/{t.Component}", new { name = "Changed" })).StatusCode);
    }
}
