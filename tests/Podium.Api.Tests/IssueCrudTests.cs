using System.Net;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>Issues (ADR-0011 §4): the fields, the project's own value sets, the references, who may, and the audit.</summary>
[Collection(ApiGroup.Name)]
public sealed class IssueCrudTests(ApiFactory api)
{
    [Fact]
    public async Task An_issue_keeps_every_field_and_numbers_count_up_inside_the_project()
    {
        var t = await IssueKit.TeamAsync(api);
        var epic = await IssueKit.IssueAsync(t.Owner, t.Project, "Checkout", "epic");

        var id = await IssueKit.IssueAsync(t.Contributor, t.Project, new
        {
            title = "Pay by card",
            type = "story",
            priority = "must",
            estimate = "5.50",
            status = "ready",
            assigneeUserId = t.Contributor.Id,
            parentId = epic,
            applicationId = t.Application,
            componentId = t.Component,
            releaseId = t.Release,
            description = "As a buyer I want to pay by card so that I can finish.",
            acceptanceCriteria = new[] { new { given = "a full cart", when = "I pay", then = "I get a receipt" } },
            technicalDesign = "Use the gateway client.",
        });
        var read = await IssueKit.ViewAsync(t.Reviewer, id);
        var issue = read.GetProperty("issue");

        Assert.Equal((2, "Pay by card", "story", "must", "5.5", "ready"),
            (issue.GetProperty("number").GetInt32(), issue.GetProperty("title").GetString(), issue.GetProperty("type").GetString(), issue.GetProperty("priority").GetString(), issue.GetProperty("estimate").GetString(), issue.GetProperty("status").GetString()));
        Assert.Equal((t.Contributor.Id, "Test Person", epic, t.Application, t.Component, t.Release),
            (issue.GetProperty("assigneeUserId").GetGuid(), issue.GetProperty("assigneeName").GetString(), issue.GetProperty("parentId").GetGuid(), issue.GetProperty("applicationId").GetGuid(), issue.GetProperty("componentId").GetGuid(), issue.GetProperty("releaseId").GetGuid()));
        Assert.Equal("Checkout", read.GetProperty("parentTitle").GetString());
        Assert.Equal("a full cart", read.GetProperty("acceptanceCriteria")[0].GetProperty("given").GetString());
        Assert.Equal(("Use the gateway client.", false, false), (read.GetProperty("technicalDesign").GetString(), read.GetProperty("canEdit").GetBoolean(), issue.GetProperty("archived").GetBoolean()));
        Assert.Equal(1, (await IssueKit.ViewAsync(t.Owner, epic)).GetProperty("issue").GetProperty("number").GetInt32());
    }

    [Fact]
    public async Task A_bare_issue_takes_the_first_item_type_of_the_project_and_the_backlog_status()
    {
        var t = await IssueKit.TeamAsync(api);

        var issue = (await IssueKit.ViewAsync(t.Owner, await IssueKit.IssueAsync(t.Owner, t.Project, new { title = "Bare" }))).GetProperty("issue");

        Assert.Equal(("epic", "backlog", JsonValueKind.Null, JsonValueKind.Null), (issue.GetProperty("type").GetString(), issue.GetProperty("status").GetString(), issue.GetProperty("priority").ValueKind, issue.GetProperty("estimate").ValueKind));
    }

    [Fact]
    public async Task Numbers_stay_unique_when_issues_are_created_at_the_same_time()
    {
        var t = await IssueKit.TeamAsync(api);

        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(n => IssueKit.IssueAsync(t.Owner, t.Project, $"Parallel {n}")));

        var numbers = new List<int>();
        foreach (var id in ids) numbers.Add((await IssueKit.ViewAsync(t.Owner, id)).GetProperty("issue").GetProperty("number").GetInt32());
        Assert.Equal(Enumerable.Range(1, 8), numbers.Order());
    }

    [Theory]
    [InlineData("title", "", "title", "invalid_name")]
    [InlineData("type", "task", "type", "invalid_value")] // the project did not enable it
    [InlineData("priority", "p0", "priority", "invalid_value")] // not on this project's scale
    [InlineData("estimate", "abc", "estimate", "invalid_value")]
    [InlineData("estimate", "1.234", "estimate", "invalid_value")]
    [InlineData("estimate", "-1", "estimate", "invalid_value")]
    [InlineData("estimate", "10000", "estimate", "invalid_value")]
    [InlineData("status", "finished", "status", "invalid_value")]
    [InlineData("severity", "bad", "severity", "invalid_value")]
    public async Task A_field_outside_its_set_is_refused_with_a_code_the_web_translates(string field, string value, string errorField, string code)
    {
        var t = await IssueKit.TeamAsync(api);
        var body = IssueKit.Merge(new() { ["title"] = "Valid", ["type"] = "bug" }, new Dictionary<string, object?> { [field] = value });

        Assert.Equal((errorField, code), await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/projects/{t.Project}/issues", body)));
    }

    [Fact]
    public async Task Long_texts_and_incomplete_criteria_are_refused_and_bug_fields_belong_to_bugs_only()
    {
        var t = await IssueKit.TeamAsync(api);
        async Task<(string, string)> Post(object extra) => await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/projects/{t.Project}/issues", IssueKit.Merge(new() { ["title"] = "X", ["type"] = "story" }, extra)));

        Assert.Equal(("description", "too_long"), await Post(new { description = new string('x', 20001) }));
        Assert.Equal(("acceptanceCriteria", "invalid_list"), await Post(new { acceptanceCriteria = new[] { new { given = "a", when = " ", then = "c" } } }));
        Assert.Equal(("reproSteps", "bug_only"), await Post(new { reproSteps = "click" }));
        Assert.Equal(("severity", "bug_only"), await Post(new { severity = "major" }));

        var bug = await IssueKit.IssueAsync(t.Owner, t.Project, "Crash", "bug", new { bugEnvironment = "prod", reproSteps = "open", expectedResult = "works", actualResult = "crash", severity = "critical" });
        var read = await IssueKit.ViewAsync(t.Owner, bug);
        Assert.Equal(("prod", "open", "works", "crash", "critical"),
            (read.GetProperty("bugEnvironment").GetString(), read.GetProperty("reproSteps").GetString(), read.GetProperty("expectedResult").GetString(), read.GetProperty("actualResult").GetString(), read.GetProperty("severity").GetString()));
    }

    [Fact]
    public async Task The_estimate_follows_the_unit_of_the_project()
    {
        var shirt = IssueKit.Merge(new() { ["name"] = "Shirts", ["estimationUnit"] = "t_shirt" }, null);
        var t = await IssueKit.TeamAsync(api, shirt);

        var id = await IssueKit.IssueAsync(t.Owner, t.Project, "Sized", "epic", new { estimate = "XL" });
        Assert.Equal("xl", (await IssueKit.ViewAsync(t.Owner, id)).GetProperty("issue").GetProperty("estimate").GetString());
        Assert.Equal(("estimate", "invalid_value"), await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/projects/{t.Project}/issues", new { title = "Numeric", estimate = "7" })));
    }

    [Fact]
    public async Task References_must_exist_in_this_project_and_be_usable()
    {
        var t = await IssueKit.TeamAsync(api);
        var other = await IssueKit.TeamAsync(api);
        var story = await IssueKit.IssueAsync(t.Owner, t.Project, "A story");
        var epic = await IssueKit.IssueAsync(t.Owner, t.Project, "An epic", "epic");
        var foreign = await IssueKit.IssueAsync(other.Owner, other.Project, "Foreign epic", "epic");
        async Task<(string, string)> Post(object extra) => await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/projects/{t.Project}/issues", IssueKit.Merge(new() { ["title"] = "X", ["type"] = "story" }, extra)));

        Assert.Equal(("assigneeUserId", "not_a_member"), await Post(new { assigneeUserId = t.Viewer.Id }));
        Assert.Equal(("parentId", "invalid_parent"), await Post(new { parentId = story }));
        Assert.Equal(("parentId", "invalid_parent"), await Post(new { parentId = foreign }));
        Assert.Equal(("parentId", "invalid_parent"), await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/projects/{t.Project}/issues", new { title = "Epic in epic", type = "epic", parentId = epic })));
        Assert.Equal(("applicationId", "invalid_application"), await Post(new { applicationId = other.Application }));
        Assert.Equal(("componentId", "invalid_component"), await Post(new { componentId = t.Component })); // a component needs its application
        Assert.Equal(("componentId", "invalid_component"), await Post(new { applicationId = t.Application, componentId = other.Component }));
        Assert.Equal(("releaseId", "invalid_release"), await Post(new { applicationId = t.Application, releaseId = other.Release }));

        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/releases/{t.Release}")).EnsureSuccessStatusCode();
        Assert.Equal(("releaseId", "invalid_release"), await Post(new { applicationId = t.Application, releaseId = t.Release }));
        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/applications/{t.Application}")).EnsureSuccessStatusCode();
        Assert.Equal(("applicationId", "invalid_application"), await Post(new { applicationId = t.Application }));
    }

    [Fact]
    public async Task A_replace_clears_what_is_missing_keeps_a_missing_priority_and_clears_an_empty_one()
    {
        var t = await IssueKit.TeamAsync(api);
        var id = await IssueKit.IssueAsync(t.Owner, t.Project, new
        {
            title = "Before",
            type = "story",
            priority = "must",
            estimate = "3",
            assigneeUserId = t.Owner.Id,
            description = "text",
            applicationId = t.Application,
            componentId = t.Component,
        });

        (await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{id}", new { title = "After" })).EnsureSuccessStatusCode();
        var kept = (await IssueKit.ViewAsync(t.Owner, id));
        var issue = kept.GetProperty("issue");
        Assert.Equal(("After", "story", "must", "3"), (issue.GetProperty("title").GetString(), issue.GetProperty("type").GetString(), issue.GetProperty("priority").GetString(), issue.GetProperty("estimate").GetString()));
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null), (issue.GetProperty("assigneeUserId").ValueKind, issue.GetProperty("componentId").ValueKind, kept.GetProperty("description").ValueKind));

        (await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{id}", new { title = "After", priority = "", estimate = "" })).EnsureSuccessStatusCode();
        var cleared = (await IssueKit.ViewAsync(t.Owner, id)).GetProperty("issue");
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (cleared.GetProperty("priority").ValueKind, cleared.GetProperty("estimate").ValueKind));
    }

    [Fact]
    public async Task An_epic_with_children_stays_an_epic_and_an_archived_issue_is_read_only_until_restored()
    {
        var t = await IssueKit.TeamAsync(api);
        var epic = await IssueKit.IssueAsync(t.Owner, t.Project, "Epic", "epic");
        var child = await IssueKit.IssueAsync(t.Owner, t.Project, "Child", "story", new { parentId = epic });

        Assert.Equal(("type", "invalid_value"), await ProjectKit.ProblemAsync(await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{epic}", new { title = "Epic", type = "story" })));

        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{child}")).EnsureSuccessStatusCode();
        Assert.True((await IssueKit.ViewAsync(t.Owner, child)).GetProperty("issue").GetProperty("archived").GetBoolean());
        Assert.Equal(("status", "archived"), await ProjectKit.ProblemAsync(await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{child}", new { title = "Child" })));
        (await t.Owner.PostAsync($"/api/issues/{child}/restore")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{child}", new { title = "Child again", parentId = epic })).StatusCode);
    }

    [Fact]
    public async Task A_contributor_works_on_issues_but_a_reviewer_a_viewer_and_a_stranger_cannot()
    {
        var t = await IssueKit.TeamAsync(api);
        var stranger = await api.NewClient().SignedUpAsync("is-stranger");
        var id = await IssueKit.IssueAsync(t.Contributor, t.Project, "Mine");

        Assert.Equal(HttpStatusCode.NoContent, (await t.Contributor.SendAsync(HttpMethod.Put, $"/api/issues/{id}", new { title = "Mine too" })).StatusCode);
        Assert.True((await IssueKit.ViewAsync(t.Contributor, id)).GetProperty("canEdit").GetBoolean());
        foreach (var reader in new[] { t.Reviewer, t.Viewer })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/projects/{t.Project}/issues", new { title = "No" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(HttpMethod.Put, $"/api/issues/{id}", new { title = "No" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(HttpMethod.Delete, $"/api/issues/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/issues/{id}/restore")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(HttpMethod.Put, $"/api/issues/{id}/position", new { afterIssueId = (Guid?)null })).StatusCode);
            Assert.False((await IssueKit.ViewAsync(reader, id)).GetProperty("canEdit").GetBoolean());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/issues/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/projects/{t.Project}/issues")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/projects/{t.Project}/issues", new { title = "No" })).StatusCode);
    }

    [Fact]
    public async Task An_archived_project_takes_no_new_issues()
    {
        var t = await IssueKit.TeamAsync(api);
        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/projects/{t.Project}")).EnsureSuccessStatusCode();

        Assert.Equal(("status", "archived"), await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/projects/{t.Project}/issues", new { title = "Late" })));
    }

    [Fact]
    public async Task Issue_changes_are_audited_by_id_without_the_typed_text()
    {
        var t = await IssueKit.TeamAsync(api);
        var id = await IssueKit.IssueAsync(t.Owner, t.Project, "Hush issue");
        (await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{id}", new { title = "Hush issue", description = "Hush text" })).EnsureSuccessStatusCode();
        (await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{id}/position", new { afterIssueId = (Guid?)null })).EnsureSuccessStatusCode();
        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{id}")).EnsureSuccessStatusCode();
        (await t.Owner.PostAsync($"/api/issues/{id}/restore")).EnsureSuccessStatusCode();

        await using var db = api.Db();
        var rows = await db.AuditEvents.AsNoTracking().Where(a => a.TargetType == "issue" && a.TargetId == id.ToString()).OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(["issue.created", "issue.updated", "issue.moved", "issue.archived", "issue.restored"], rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.DoesNotContain("Hush", r.Detail?.RootElement.GetRawText() ?? "", StringComparison.Ordinal));
    }
}
