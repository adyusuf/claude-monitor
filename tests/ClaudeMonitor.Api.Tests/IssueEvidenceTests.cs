using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The test scenarios of an issue with their results, and its pull request and commit links (ADR-0011 §4).</summary>
[Collection(ApiGroup.Name)]
public sealed class IssueEvidenceTests(ApiFactory api)
{
    private static async Task<Guid> IdAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    [Fact]
    public async Task A_scenario_starts_not_run_takes_results_and_remembers_who_set_the_last_one()
    {
        var t = await IssueKit.TeamAsync(api);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Tested");

        var created = await t.Contributor.PostAsync($"/api/issues/{issue}/test-cases", new { title = "Pay with a valid card", steps = "1. open 2. pay", expected = "a receipt" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var scenario = await IdAsync(created);
        var first = (await t.Reviewer.GetJsonAsync($"/api/issues/{issue}/test-cases"))[0];
        Assert.Equal(("Pay with a valid card", "not_run", JsonValueKind.Null), (first.GetProperty("title").GetString(), first.GetProperty("result").GetString(), first.GetProperty("resultBy").ValueKind));

        Assert.Equal(HttpStatusCode.NoContent, (await t.Contributor.PostAsync($"/api/test-cases/{scenario}/result", new { result = "failed", note = "declined" })).StatusCode);
        var failed = (await t.Reviewer.GetJsonAsync($"/api/issues/{issue}/test-cases"))[0];
        Assert.Equal(("failed", "declined", t.Contributor.Id, "Test Person"), (failed.GetProperty("result").GetString(), failed.GetProperty("resultNote").GetString(), failed.GetProperty("resultBy").GetGuid(), failed.GetProperty("resultByName").GetString()));
        Assert.NotEqual(JsonValueKind.Null, failed.GetProperty("resultAt").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.PostAsync($"/api/test-cases/{scenario}/result", new { result = "passed" })).StatusCode);
        Assert.Equal("passed", (await t.Owner.GetJsonAsync($"/api/issues/{issue}/test-cases"))[0].GetProperty("result").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Put, $"/api/test-cases/{scenario}", new { title = "Pay with a card" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/test-cases/{scenario}")).StatusCode);
        Assert.Empty((await t.Owner.GetJsonAsync($"/api/issues/{issue}/test-cases")).EnumerateArray());
    }

    [Fact]
    public async Task Scenarios_and_results_with_bad_values_are_refused_and_only_workers_change_them()
    {
        var t = await IssueKit.TeamAsync(api);
        var stranger = await api.NewClient().SignedUpAsync("ev-stranger");
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Tested");
        var scenario = await IdAsync(await t.Owner.PostAsync($"/api/issues/{issue}/test-cases", new { title = "One" }));

        Assert.Equal(("title", "invalid_name"), await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/issues/{issue}/test-cases", new { title = " " })));
        Assert.Equal(("steps", "too_long"), await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/issues/{issue}/test-cases", new { title = "T", steps = new string('x', 5001) })));
        Assert.Equal(("expected", "too_long"), await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/issues/{issue}/test-cases", new { title = "T", expected = new string('x', 2001) })));
        Assert.Equal(("result", "invalid_value"), await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/test-cases/{scenario}/result", new { result = "great" })));
        Assert.Equal(("note", "too_long"), await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/test-cases/{scenario}/result", new { result = "passed", note = new string('x', 20001) })));

        foreach (var reader in new[] { t.Reviewer, t.Viewer })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/issues/{issue}/test-cases", new { title = "No" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(HttpMethod.Put, $"/api/test-cases/{scenario}", new { title = "No" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/test-cases/{scenario}/result", new { result = "passed" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(HttpMethod.Delete, $"/api/test-cases/{scenario}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/issues/{issue}/test-cases")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/test-cases/{scenario}/result", new { result = "passed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(HttpMethod.Put, $"/api/test-cases/{scenario}", new { title = "No" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(HttpMethod.Delete, $"/api/test-cases/{scenario}")).StatusCode);
    }

    [Fact]
    public async Task A_pull_request_or_commit_link_must_be_an_https_address_without_a_password_and_is_not_added_twice()
    {
        var t = await IssueKit.TeamAsync(api);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Linked");

        var added = await t.Contributor.PostAsync($"/api/issues/{issue}/code-links", new { kind = "pr", url = "https://git.example.invalid/team/shop/pull/12", title = "Add card payment" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var row = (await t.Reviewer.GetJsonAsync($"/api/issues/{issue}/code-links"))[0];
        Assert.Equal(("pr", "manual", "Add card payment"), (row.GetProperty("kind").GetString(), row.GetProperty("source").GetString(), row.GetProperty("title").GetString()));

        Assert.Equal(("url", "duplicate_link"), await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/issues/{issue}/code-links", new { kind = "pr", url = "https://git.example.invalid/team/shop/pull/12" })));
        foreach (var bad in new[] { "http://git.example.invalid/x", "javascript:alert(1)", "https://user:pw@git.example.invalid/x", "ssh://git.example.invalid/x", "", "not a url" })
        {
            Assert.Equal(("url", "invalid_url"), await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/issues/{issue}/code-links", new { kind = "commit", url = bad })));
        }

        Assert.Equal(("kind", "invalid_value"), await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/issues/{issue}/code-links", new { kind = "branch", url = "https://git.example.invalid/b" })));
        Assert.Equal(("title", "too_long"), await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/issues/{issue}/code-links", new { kind = "pr", url = "https://git.example.invalid/c", title = new string('x', 201) })));
        Assert.Equal(HttpStatusCode.Created, (await t.Owner.PostAsync($"/api/issues/{issue}/code-links", new { kind = "commit", url = "https://git.example.invalid/team/shop/commit/abc123" })).StatusCode);
    }

    [Fact]
    public async Task A_code_link_is_removed_by_a_worker_only()
    {
        var t = await IssueKit.TeamAsync(api);
        var stranger = await api.NewClient().SignedUpAsync("ev-link-stranger");
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Linked");
        var link = await IdAsync(await t.Owner.PostAsync($"/api/issues/{issue}/code-links", new { kind = "pr", url = "https://git.example.invalid/pull/1" }));

        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PostAsync($"/api/issues/{issue}/code-links", new { kind = "pr", url = "https://git.example.invalid/pull/2" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Reviewer.SendAsync(HttpMethod.Delete, $"/api/code-links/{link}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(HttpMethod.Delete, $"/api/code-links/{link}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/issues/{issue}/code-links")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t.Contributor.SendAsync(HttpMethod.Delete, $"/api/code-links/{link}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/code-links/{link}")).StatusCode);
        Assert.Empty((await t.Owner.GetJsonAsync($"/api/issues/{issue}/code-links")).EnumerateArray());
    }

    [Fact]
    public async Task Evidence_changes_are_audited_by_issue_id_and_the_typed_text_stays_out_of_the_trail()
    {
        var t = await IssueKit.TeamAsync(api);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Audited");
        var scenario = await IdAsync(await t.Owner.PostAsync($"/api/issues/{issue}/test-cases", new { title = "Hush scenario" }));
        (await t.Owner.PostAsync($"/api/test-cases/{scenario}/result", new { result = "passed", note = "Hush note" })).EnsureSuccessStatusCode();
        (await t.Owner.PostAsync($"/api/issues/{issue}/code-links", new { kind = "pr", url = "https://git.example.invalid/hush/1", title = "Hush pr" })).EnsureSuccessStatusCode();
        var other = await IssueKit.IssueAsync(t.Owner, t.Project, "Other");
        (await t.Owner.PostAsync($"/api/issues/{issue}/links", new { otherIssueId = other, kind = "blocks" })).EnsureSuccessStatusCode();

        await using var db = api.Db();
        var rows = await db.AuditEvents.AsNoTracking().Where(a => a.TargetType == "issue" && a.TargetId == issue.ToString()).OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(["issue.created", "issue.test_case_changed", "issue.test_case_changed", "issue.code_link_changed", "issue.link_changed"], rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.DoesNotContain("Hush", r.Detail?.RootElement.GetRawText() ?? "", StringComparison.Ordinal));
    }
}
