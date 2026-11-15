using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>An archived issue is read-only for its evidence too: test cases, results and code links (the links and runs already refuse it).</summary>
[Collection(ApiGroup.Name)]
public sealed class IssueEvidenceArchivedTests(ApiFactory api)
{
    private static async Task<Guid> IdAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    [Fact]
    public async Task An_archived_issue_takes_no_test_case_result_or_code_link_change_but_can_still_be_read()
    {
        var t = await IssueKit.TeamAsync(api);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Kept");
        var scenario = await IdAsync(await t.Owner.PostAsync($"/api/issues/{issue}/test-cases", new { title = "Before" }));
        var link = await IdAsync(await t.Owner.PostAsync($"/api/issues/{issue}/code-links", new { kind = "pr", url = "https://git.example.invalid/pull/1" }));
        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{issue}")).EnsureSuccessStatusCode();
        var archived = ("status", "archived");

        Assert.Equal(archived, await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/issues/{issue}/test-cases", new { title = "After" })));
        Assert.Equal(archived, await ProjectKit.ProblemAsync(await t.Owner.SendAsync(HttpMethod.Put, $"/api/test-cases/{scenario}", new { title = "Renamed" })));
        Assert.Equal(archived, await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/test-cases/{scenario}/result", new { result = "passed" })));
        Assert.Equal(archived, await ProjectKit.ProblemAsync(await t.Owner.SendAsync(HttpMethod.Delete, $"/api/test-cases/{scenario}")));
        Assert.Equal(archived, await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/issues/{issue}/code-links", new { kind = "pr", url = "https://git.example.invalid/pull/2" })));
        Assert.Equal(archived, await ProjectKit.ProblemAsync(await t.Owner.SendAsync(HttpMethod.Delete, $"/api/code-links/{link}")));

        var cases = await t.Owner.GetJsonAsync($"/api/issues/{issue}/test-cases");
        Assert.Equal(("Before", "not_run"), (cases[0].GetProperty("title").GetString(), cases[0].GetProperty("result").GetString()));
        Assert.Single((await t.Owner.GetJsonAsync($"/api/issues/{issue}/code-links")).EnumerateArray());
    }

    [Fact]
    public async Task A_restored_issue_takes_evidence_changes_again()
    {
        var t = await IssueKit.TeamAsync(api);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Back");
        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{issue}")).EnsureSuccessStatusCode();
        (await t.Owner.PostAsync($"/api/issues/{issue}/restore", new { })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Created, (await t.Owner.PostAsync($"/api/issues/{issue}/test-cases", new { title = "Again" })).StatusCode);
    }
}
