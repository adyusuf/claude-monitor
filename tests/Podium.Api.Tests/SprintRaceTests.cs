using System.Net;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>Sprint changes made at the same moment: the project's row lock puts them in a line, so no rule is broken in between (ADR-0011 §5).</summary>
[Collection(ApiGroup.Name)]
public sealed class SprintRaceTests(ApiFactory api)
{
    private const int Rounds = 5;

    [Fact]
    public async Task Of_several_sprints_started_at_once_in_one_scope_exactly_one_becomes_active()
    {
        var t = await IssueKit.TeamAsync(api);
        var sprints = new List<Guid>();
        for (var n = 0; n < 4; n++) sprints.Add(await SprintKit.SprintAsync(t.Owner, t.Project));

        var answers = await Task.WhenAll(sprints.Select(id => SprintKit.StartAsync(t.Owner, id)));

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.NoContent);
        foreach (var refused in answers.Where(a => a.StatusCode != HttpStatusCode.NoContent)) Assert.Equal(("status", "sprint_active"), await ProjectKit.ProblemAsync(refused));
        await using var db = api.Db();
        Assert.Equal(1, await db.Sprints.CountAsync(s => s.ProjectId == t.Project && s.Status == SprintStatuses.Active));
    }

    [Fact]
    public async Task Sprints_created_at_once_and_while_a_sync_imports_sprints_get_different_numbers()
    {
        var later = new List<FakeSprint>();
        var w = await JiraWriteKit.OpenAsync(api, (site, space) => later.AddRange(Enumerable.Range(0, 3).Select(n => site.Sprint(space, $"Later {n}", "future"))));

        var created = Task.WhenAll(Enumerable.Range(0, 6).Select(n => w.Member.PostAsync($"/api/projects/{w.Project}/sprints", new { name = $"Parallel {n}" })));
        var imported = JiraKit.ImportSprintsAsync(w.World, JiraWriteKit.Key, [.. later.Select(x => x.Id)]);
        var pass = JiraKit.PassAsync(w.World);
        await Task.WhenAll(created, imported, pass);

        Assert.All(await created, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var numbers = (await JiraKit.SprintsAsync(w.World, w.Project)).Select(s => s.Number).ToList();
        Assert.Equal(9, numbers.Count);
        Assert.Equal(numbers.Count, numbers.Distinct().Count());
    }

    [Fact]
    public async Task An_issue_added_while_the_sprint_closes_never_stays_unfinished_in_the_closed_sprint()
    {
        var t = await IssueKit.TeamAsync(api);
        for (var round = 0; round < Rounds; round++)
        {
            var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);
            (await SprintKit.StartAsync(t.Owner, sprint)).EnsureSuccessStatusCode();
            var late = await IssueKit.IssueAsync(t.Owner, t.Project, $"Late {round}");

            await Task.WhenAll(SprintKit.CloseAsync(t.Owner, sprint), SprintKit.AddAsync(t.Owner, sprint, late));

            Assert.Equal("closed", await SprintKit.StatusAsync(t.Owner, sprint));
            Assert.Null(await SprintKit.IssueSprintAsync(t.Owner, late));
        }
    }

    [Fact]
    public async Task An_issue_added_while_the_sprint_is_deleted_never_points_at_the_deleted_sprint()
    {
        var t = await IssueKit.TeamAsync(api);
        for (var round = 0; round < Rounds; round++)
        {
            var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);
            var late = await IssueKit.IssueAsync(t.Owner, t.Project, $"Late {round}");

            var answers = await Task.WhenAll(t.Owner.SendAsync(HttpMethod.Delete, $"/api/sprints/{sprint}"), SprintKit.AddAsync(t.Owner, sprint, late));

            Assert.Equal(HttpStatusCode.NoContent, answers[0].StatusCode);
            Assert.Null(await SprintKit.IssueSprintAsync(t.Owner, late));
        }
    }

    [Fact]
    public async Task A_sprint_started_while_it_is_deleted_is_never_left_active_and_archived()
    {
        var t = await IssueKit.TeamAsync(api);
        for (var round = 0; round < Rounds; round++)
        {
            var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);

            await Task.WhenAll(SprintKit.StartAsync(t.Owner, sprint), t.Owner.SendAsync(HttpMethod.Delete, $"/api/sprints/{sprint}"));

            await using var db = api.Db();
            var row = await db.Sprints.AsNoTracking().SingleAsync(s => s.Id == sprint);
            Assert.False(row.Status == SprintStatuses.Active && row.ArchivedAt is not null, $"round {round}: active and archived");
            if (row.Status == SprintStatuses.Active) (await SprintKit.CloseAsync(t.Owner, sprint)).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task A_sprint_whose_dates_are_cleared_while_it_starts_is_never_left_active_without_dates()
    {
        var t = await IssueKit.TeamAsync(api);
        for (var round = 0; round < Rounds; round++)
        {
            var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);

            await Task.WhenAll(SprintKit.StartAsync(t.Owner, sprint), t.Owner.SendAsync(HttpMethod.Put, $"/api/sprints/{sprint}", new { goal = "Ship it" }));

            await using var db = api.Db();
            var row = await db.Sprints.AsNoTracking().SingleAsync(s => s.Id == sprint);
            Assert.False(row.Status == SprintStatuses.Active && (row.StartDate is null || row.EndDate is null), $"round {round}: active without dates");
        }
    }

    [Fact]
    public async Task A_sprint_updated_while_it_closes_is_never_rewritten_after_it_closed()
    {
        var t = await IssueKit.TeamAsync(api);
        for (var round = 0; round < Rounds; round++)
        {
            var sprint = await SprintKit.SprintAsync(t.Owner, t.Project);
            (await SprintKit.StartAsync(t.Owner, sprint)).EnsureSuccessStatusCode();

            await Task.WhenAll(SprintKit.CloseAsync(t.Owner, sprint), t.Owner.SendAsync(HttpMethod.Put, $"/api/sprints/{sprint}", new { goal = $"Late {round}", startDate = "2026-11-02", endDate = "2026-11-13" }));

            await using var db = api.Db();
            var row = await db.Sprints.AsNoTracking().SingleAsync(s => s.Id == sprint);
            Assert.Equal(SprintStatuses.Closed, row.Status);
            Assert.Equal(row.ClosedAt, row.UpdatedAt);
        }
    }
}
