using System.Net;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>A project settings change that would leave issues with a type, priority or estimate the project no longer offers is refused with 409 and a code that names the setting (F-102 B).</summary>
[Collection(ApiGroup.Name)]
public sealed class ProjectSettingsInUseTests(ApiFactory api)
{
    private static readonly string[] Fields =
    [
        "name", "purpose", "ownerUserId", "sponsor", "stakeholders", "budgetAmount", "budgetCurrency", "startDate", "endDate", "itemTypes", "priorityScale",
        "estimationUnit", "sprintLengthDays", "definitionOfReady", "definitionOfDone",
    ];

    /// <summary>The project form as the web sends it back, with some settings changed.</summary>
    private static async Task<HttpResponseMessage> EditAsync(TestUser who, Guid project, object changes)
    {
        var view = await who.GetJsonAsync($"/api/projects/{project}");
        var body = Fields.ToDictionary(f => f, f => (object?)view.GetProperty(f));
        return await who.SendAsync(HttpMethod.Put, $"/api/projects/{project}", IssueKit.Merge(body, changes));
    }

    private static async Task<(HttpStatusCode Status, string? Title, int? Count)> AnswerAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        var body = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
        var count = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("count", out var n) ? n.GetInt32() : (int?)null;
        return (response.StatusCode, body.ValueKind == JsonValueKind.Object && body.TryGetProperty("title", out var t) ? t.GetString() : null, count);
    }

    private async Task<(IssueTeam Team, TestUser Who)> TeamAsync()
    {
        var t = await IssueKit.TeamAsync(api, ProjectKit.Project("Settings")); // epic, story, bug; moscow; hours
        return (t, t.Owner);
    }

    [Fact]
    public async Task Removing_an_item_type_that_issues_use_is_refused_and_naming_the_count_and_one_nobody_uses_is_allowed()
    {
        var (t, who) = await TeamAsync();
        await IssueKit.IssueAsync(who, t.Project, "A story", "story");
        var archived = await IssueKit.IssueAsync(who, t.Project, "An old bug", "bug");
        (await who.SendAsync(HttpMethod.Delete, $"/api/issues/{archived}")).EnsureSuccessStatusCode(); // archived, but a restore would bring it back

        Assert.Equal((HttpStatusCode.Conflict, "item_type_in_use", 2), await AnswerAsync(await EditAsync(who, t.Project, new { itemTypes = new[] { "epic" } })));
        Assert.Equal((HttpStatusCode.Conflict, "item_type_in_use", 1), await AnswerAsync(await EditAsync(who, t.Project, new { itemTypes = new[] { "epic", "bug" } })));
        Assert.Equal(HttpStatusCode.NoContent, (await EditAsync(who, t.Project, new { itemTypes = new[] { "story", "bug", "task" } })).StatusCode); // epic is unused
        Assert.Equal(["story", "bug", "task"], (await who.GetJsonAsync($"/api/projects/{t.Project}")).GetProperty("itemTypes").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Changing_the_priority_scale_is_refused_while_an_issue_holds_a_priority_the_new_scale_lacks()
    {
        var (t, who) = await TeamAsync();
        await IssueKit.IssueAsync(who, t.Project, new { title = "Urgent", type = "story", priority = "must" });

        Assert.Equal((HttpStatusCode.Conflict, "priority_scale_in_use", 1), await AnswerAsync(await EditAsync(who, t.Project, new { priorityScale = "p0_p3" })));
        Assert.Equal(HttpStatusCode.NoContent, (await EditAsync(who, t.Project, new { priorityScale = "moscow", sprintLengthDays = 7 })).StatusCode); // the same scale
    }

    [Fact]
    public async Task Changing_the_priority_scale_is_allowed_when_no_issue_holds_a_priority()
    {
        var (t, who) = await TeamAsync();
        await IssueKit.IssueAsync(who, t.Project, "No priority", "story");

        Assert.Equal(HttpStatusCode.NoContent, (await EditAsync(who, t.Project, new { priorityScale = "p0_p3" })).StatusCode);
    }

    [Fact]
    public async Task Changing_the_estimation_unit_is_refused_when_an_estimate_does_not_fit_the_new_unit_and_allowed_between_numeric_units()
    {
        var (t, who) = await TeamAsync();
        await IssueKit.IssueAsync(who, t.Project, new { title = "Sized", type = "story", estimate = "5" });

        Assert.Equal((HttpStatusCode.Conflict, "estimation_unit_in_use", 1), await AnswerAsync(await EditAsync(who, t.Project, new { estimationUnit = "t_shirt" })));
        Assert.Equal(HttpStatusCode.NoContent, (await EditAsync(who, t.Project, new { estimationUnit = "story_points" })).StatusCode); // hours to points keeps every number valid
    }

    [Fact]
    public async Task A_settings_change_that_strands_nothing_in_another_project_is_not_blocked_by_that_projects_issues()
    {
        var (t, who) = await TeamAsync();
        var other = (await ProjectKit.CreateProjectAsync(who, "Other")).GetProperty("id").GetGuid();
        await IssueKit.IssueAsync(who, other, new { title = "Elsewhere", type = "bug", priority = "must" });

        Assert.Equal(HttpStatusCode.NoContent, (await EditAsync(who, t.Project, new { itemTypes = new[] { "epic", "story" } })).StatusCode);
    }

    [Fact]
    public async Task A_form_without_the_settings_keeps_them_and_is_never_refused_for_them()
    {
        var (t, who) = await TeamAsync();
        await IssueKit.IssueAsync(who, t.Project, new { title = "Held", type = "bug", priority = "should", estimate = "3" });

        var response = await who.SendAsync(HttpMethod.Put, $"/api/projects/{t.Project}", new { name = "Settings", sprintLengthDays = 9 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
