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
}
