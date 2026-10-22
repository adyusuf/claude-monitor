using System.Net;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>Sprints (ADR-0011 §5): the definition, the life cycle planned - active - closed, and who may.</summary>
[Collection(ApiGroup.Name)]
public sealed class SprintTests(ApiFactory api)
{
    [Fact]
    public async Task A_sprint_keeps_every_field_and_numbers_count_up_inside_the_project()
    {
        var t = await IssueKit.TeamAsync(api);

        var first = await SprintKit.SprintAsync(t.Contributor, t.Project, new
        {
            name = "Warm-up",
            goal = "Cart works",
            startDate = "2026-11-02",
            endDate = "2026-11-13",
            capacity = 40.5m,
            releaseId = t.Release,
            teamUserIds = new[] { t.Owner.Id, t.Contributor.Id },
        });
        var second = await SprintKit.SprintAsync(t.Owner, t.Project);
        var view = await SprintKit.ViewAsync(t.Reviewer, first);
        var sprint = view.GetProperty("sprint");

        Assert.Equal((1, "Warm-up", "Cart works", "2026-11-02", "2026-11-13", 40.5m, "planned"),
            (sprint.GetProperty("number").GetInt32(), sprint.GetProperty("name").GetString(), sprint.GetProperty("goal").GetString(), sprint.GetProperty("startDate").GetString(),
                sprint.GetProperty("endDate").GetString(), sprint.GetProperty("capacity").GetDecimal(), sprint.GetProperty("status").GetString()));
        Assert.Equal((t.Release, "1.0.0", 0), (sprint.GetProperty("releaseId").GetGuid(), sprint.GetProperty("releaseName").GetString(), sprint.GetProperty("issueCount").GetInt32()));
        Assert.Equal(2, view.GetProperty("team").GetArrayLength());
        Assert.False(view.GetProperty("canEdit").GetBoolean());
        Assert.Equal(2, (await SprintKit.ViewAsync(t.Owner, second)).GetProperty("sprint").GetProperty("number").GetInt32());
    }

    [Theory]
    [InlineData("name", "NAME_LONG", "name", "too_long")]
    [InlineData("goal", "GOAL_LONG", "goal", "too_long")]
    [InlineData("capacity", -1, "capacity", "out_of_range")]
    [InlineData("capacity", 1.234, "capacity", "out_of_range")]
    public async Task A_field_outside_its_limits_is_refused(string field, object value, string errorField, string code)
    {
        var t = await IssueKit.TeamAsync(api);
        var body = IssueKit.Merge(new() { ["goal"] = "ok" }, new Dictionary<string, object?> { [field] = value switch { "NAME_LONG" => new string('x', 101), "GOAL_LONG" => new string('x', 1001), _ => value } });

        Assert.Equal((errorField, code), await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/projects/{t.Project}/sprints", body)));
    }

    [Fact]
    public async Task Dates_the_team_the_release_and_the_application_are_checked()
    {
        var t = await IssueKit.TeamAsync(api);
        var other = await IssueKit.TeamAsync(api);
        var stranger = await api.NewClient().SignedUpAsync("sp-outsider");
        async Task<(string, string)> Post(object body) => await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/projects/{t.Project}/sprints", body));

        Assert.Equal(("endDate", "before_start"), await Post(new { startDate = "2026-11-13", endDate = "2026-11-02" }));
        Assert.Equal(("teamUserIds", "invalid_list"), await Post(new { teamUserIds = new[] { t.Owner.Id, t.Owner.Id } }));
        Assert.Equal(("teamUserIds", "not_a_member"), await Post(new { teamUserIds = new[] { stranger.Id } }));
        Assert.Equal(("teamUserIds", "not_a_member"), await Post(new { teamUserIds = new[] { t.Viewer.Id } }));
        Assert.Equal(("releaseId", "invalid_release"), await Post(new { releaseId = other.Release }));
        Assert.Equal(("applicationId", "invalid_application"), await Post(new { applicationId = other.Application }));
        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/releases/{t.Release}")).EnsureSuccessStatusCode();
        Assert.Equal(("releaseId", "invalid_release"), await Post(new { releaseId = t.Release }));
    }

    [Fact]
    public async Task A_release_of_another_application_does_not_fit_a_sprint_of_this_one()
    {
        var t = await IssueKit.TeamAsync(api);
        var second = (await ProjectKit.CreateApplicationAsync(t.Owner, t.Project, "Mobile")).GetProperty("id").GetGuid();

        Assert.Equal(("releaseId", "invalid_release"), await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/projects/{t.Project}/sprints", new { applicationId = second, releaseId = t.Release })));
        Assert.Equal(HttpStatusCode.Created, (await t.Owner.PostAsync($"/api/projects/{t.Project}/sprints", new { applicationId = t.Application, releaseId = t.Release })).StatusCode);
    }

    [Fact]
    public async Task A_contributor_works_on_sprints_but_a_reviewer_a_viewer_and_a_stranger_cannot()
    {
        var t = await IssueKit.TeamAsync(api);
        var stranger = await api.NewClient().SignedUpAsync("sp-stranger");
        var sprint = await SprintKit.SprintAsync(t.Contributor, t.Project);

        Assert.True((await SprintKit.ViewAsync(t.Contributor, sprint)).GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await t.Contributor.SendAsync(HttpMethod.Put, $"/api/sprints/{sprint}", new { goal = "Changed" })).StatusCode);
        foreach (var reader in new[] { t.Reviewer, t.Viewer })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/projects/{t.Project}/sprints", new { goal = "No" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(HttpMethod.Put, $"/api/sprints/{sprint}", new { goal = "No" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SprintKit.StartAsync(reader, sprint)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SprintKit.CloseAsync(reader, sprint)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(HttpMethod.Delete, $"/api/sprints/{sprint}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/sprints/{sprint}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/projects/{t.Project}/sprints")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SprintKit.StartAsync(stranger, sprint)).StatusCode);
    }

    [Fact]
    public async Task A_replace_clears_what_is_missing_keeps_the_scope_and_a_closed_sprint_is_read_only()
    {
        var t = await IssueKit.TeamAsync(api);
        var sprint = await SprintKit.SprintAsync(t.Owner, t.Project, new { name = "N", goal = "G", startDate = "2026-11-02", endDate = "2026-11-13", capacity = 5, applicationId = t.Application, releaseId = t.Release, teamUserIds = new[] { t.Owner.Id } });

        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Put, $"/api/sprints/{sprint}", new { startDate = "2026-11-02", endDate = "2026-11-20", applicationId = Guid.NewGuid() })).StatusCode);
        var view = await SprintKit.ViewAsync(t.Owner, sprint);
        var s = view.GetProperty("sprint");
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null, "2026-11-20", t.Application),
            (s.GetProperty("name").ValueKind, s.GetProperty("goal").ValueKind, s.GetProperty("capacity").ValueKind, s.GetProperty("releaseId").ValueKind, s.GetProperty("endDate").GetString(), s.GetProperty("applicationId").GetGuid()));
        Assert.Equal(0, view.GetProperty("team").GetArrayLength());

        (await SprintKit.StartAsync(t.Owner, sprint)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Put, $"/api/sprints/{sprint}", new { goal = "Still editable", startDate = "2026-11-02", endDate = "2026-11-20" })).StatusCode);
        (await SprintKit.CloseAsync(t.Owner, sprint)).EnsureSuccessStatusCode();
        Assert.Equal(("status", "closed"), await ProjectKit.ProblemAsync(await t.Owner.SendAsync(HttpMethod.Put, $"/api/sprints/{sprint}", new { goal = "No" })));
    }

    [Fact]
    public async Task Starting_needs_both_dates_and_only_one_sprint_per_scope_is_active_at_a_time()
    {
        var t = await IssueKit.TeamAsync(api);
        var second = (await ProjectKit.CreateApplicationAsync(t.Owner, t.Project, "Mobile")).GetProperty("id").GetGuid();
        var undated = await SprintKit.SprintAsync(t.Owner, t.Project, new { goal = "No dates" });
        var one = await SprintKit.SprintAsync(t.Owner, t.Project);
        var two = await SprintKit.SprintAsync(t.Owner, t.Project);
        var webSprint = await SprintKit.SprintAsync(t.Owner, t.Project, new { applicationId = t.Application, startDate = "2026-11-02", endDate = "2026-11-13" });
        var webSprint2 = await SprintKit.SprintAsync(t.Owner, t.Project, new { applicationId = t.Application, startDate = "2026-11-02", endDate = "2026-11-13" });
        var mobileSprint = await SprintKit.SprintAsync(t.Owner, t.Project, new { applicationId = second, startDate = "2026-11-02", endDate = "2026-11-13" });

        Assert.Equal(("startDate", "dates_required"), await ProjectKit.ProblemAsync(await SprintKit.StartAsync(t.Owner, undated)));
        Assert.Equal(HttpStatusCode.NoContent, (await SprintKit.StartAsync(t.Owner, one)).StatusCode);
        Assert.Equal(("status", "sprint_active"), await ProjectKit.ProblemAsync(await SprintKit.StartAsync(t.Owner, two)));
        Assert.Equal(("status", "not_planned"), await ProjectKit.ProblemAsync(await SprintKit.StartAsync(t.Owner, one)));
        Assert.Equal(HttpStatusCode.NoContent, (await SprintKit.StartAsync(t.Owner, webSprint)).StatusCode); // another scope
        Assert.Equal(HttpStatusCode.NoContent, (await SprintKit.StartAsync(t.Owner, mobileSprint)).StatusCode);
        Assert.Equal(("status", "sprint_active"), await ProjectKit.ProblemAsync(await SprintKit.StartAsync(t.Owner, webSprint2)));

        (await SprintKit.CloseAsync(t.Owner, one)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await SprintKit.StartAsync(t.Owner, two)).StatusCode);
        Assert.Equal("active", await SprintKit.StatusAsync(t.Owner, two));
        Assert.NotEqual(JsonValueKind.Null, (await SprintKit.ViewAsync(t.Owner, two)).GetProperty("sprint").GetProperty("startedAt").ValueKind);
    }

    [Fact]
    public async Task A_planned_sprint_is_deleted_and_its_issues_return_to_the_backlog_while_an_active_one_stays()
    {
        var t = await IssueKit.TeamAsync(api);
        var planned = await SprintKit.SprintAsync(t.Owner, t.Project);
        var active = await SprintKit.SprintAsync(t.Owner, t.Project);
        (await SprintKit.StartAsync(t.Owner, active)).EnsureSuccessStatusCode();
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "In a sprint");
        (await SprintKit.AddAsync(t.Owner, planned, issue)).EnsureSuccessStatusCode();

        Assert.Equal(("status", "not_planned"), await ProjectKit.ProblemAsync(await t.Owner.SendAsync(HttpMethod.Delete, $"/api/sprints/{active}")));
        Assert.Equal(planned, await SprintKit.IssueSprintAsync(t.Owner, issue));
        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/sprints/{planned}")).StatusCode);

        Assert.Null(await SprintKit.IssueSprintAsync(t.Owner, issue));
        Assert.Equal(HttpStatusCode.NotFound, (await t.Owner.Http.GetAsync($"/api/sprints/{planned}")).StatusCode);
        Assert.Equal(["In a sprint"], await IssueKit.TitlesAsync(t.Owner, $"/api/projects/{t.Project}/issues?scope=backlog"));
        Assert.Equal(HttpStatusCode.NotFound, (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/sprints/{planned}")).StatusCode);
    }

    [Fact]
    public async Task The_list_filters_by_status_and_scope_pages_newest_first_and_hides_deleted_sprints()
    {
        var t = await IssueKit.TeamAsync(api);
        var s1 = await SprintKit.SprintAsync(t.Owner, t.Project);
        await SprintKit.SprintAsync(t.Owner, t.Project, new { applicationId = t.Application });
        await SprintKit.SprintAsync(t.Owner, t.Project);
        var gone = await SprintKit.SprintAsync(t.Owner, t.Project);
        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/sprints/{gone}")).EnsureSuccessStatusCode();
        (await SprintKit.StartAsync(t.Owner, s1)).EnsureSuccessStatusCode();
        static IEnumerable<int> Numbers(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("number").GetInt32());

        Assert.Equal([3, 2, 1], Numbers(await t.Reviewer.GetJsonAsync($"/api/projects/{t.Project}/sprints")));
        Assert.Equal([1], Numbers(await t.Owner.GetJsonAsync($"/api/projects/{t.Project}/sprints?status=active")));
        Assert.Equal([3, 2], Numbers(await t.Owner.GetJsonAsync($"/api/projects/{t.Project}/sprints?status=planned")));
        Assert.Equal([2], Numbers(await t.Owner.GetJsonAsync($"/api/projects/{t.Project}/sprints?application={t.Application}")));
        var first = await t.Owner.GetJsonAsync($"/api/projects/{t.Project}/sprints?limit=2");
        Assert.Equal([3, 2], Numbers(first));
        Assert.Equal([1], Numbers(await t.Owner.GetJsonAsync($"/api/projects/{t.Project}/sprints?limit=2&cursor={first.GetProperty("next").GetString()}")));
    }

    [Fact]
    public async Task Sprint_changes_are_audited_by_id_without_the_typed_text()
    {
        var t = await IssueKit.TeamAsync(api);
        var sprint = await SprintKit.SprintAsync(t.Owner, t.Project, new { goal = "Hush goal", startDate = "2026-11-02", endDate = "2026-11-13" });
        (await t.Owner.SendAsync(HttpMethod.Put, $"/api/sprints/{sprint}", new { goal = "Hush goal 2", startDate = "2026-11-02", endDate = "2026-11-13" })).EnsureSuccessStatusCode();
        (await SprintKit.StartAsync(t.Owner, sprint)).EnsureSuccessStatusCode();
        (await SprintKit.CloseAsync(t.Owner, sprint)).EnsureSuccessStatusCode();
        var gone = await SprintKit.SprintAsync(t.Owner, t.Project);
        (await t.Owner.SendAsync(HttpMethod.Delete, $"/api/sprints/{gone}")).EnsureSuccessStatusCode();

        await using var db = api.Db();
        var rows = await db.AuditEvents.AsNoTracking().Where(a => a.TargetType == "sprint" && a.TargetId == sprint.ToString()).OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(["sprint.created", "sprint.updated", "sprint.started", "sprint.closed"], rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.DoesNotContain("Hush", r.Detail?.RootElement.GetRawText() ?? "", StringComparison.Ordinal));
        Assert.Single(await db.AuditEvents.AsNoTracking().Where(a => a.Action == "sprint.archived" && a.TargetId == gone.ToString()).ToListAsync());
    }
}
