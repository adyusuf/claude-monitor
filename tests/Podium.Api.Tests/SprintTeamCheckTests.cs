using System.Data.Common;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Podium.Api.Tests;

/// <summary>Checking the team of a sprint reads the memberships once, however many people join.</summary>
[Collection(ApiGroup.Name)]
public sealed class SprintTeamCheckTests(ApiFactory api)
{
    [Fact]
    public async Task Checking_a_sprint_team_costs_the_same_number_of_membership_queries_for_one_person_or_three()
    {
        var t = await IssueKit.TeamAsync(api);
        var count = new QueryCount("FROM workspace_members");
        await using var host = HostKit.WithInterceptor(api, count);
        var owner = await HostKit.SignInAsync(api, host, t.Owner);
        Task<HttpResponseMessage> Create(params Guid[] team) => owner.PostAsync($"/api/projects/{t.Project}/sprints", new { goal = "Team", teamUserIds = team });

        count.Reset();
        (await Create(t.Contributor.Id)).EnsureSuccessStatusCode();
        var one = count.Hits;
        count.Reset();
        (await Create(t.Owner.Id, t.Contributor.Id, t.Reviewer.Id)).EnsureSuccessStatusCode();

        Assert.Equal(one, count.Hits);
        Assert.Equal(("teamUserIds", "not_a_member"), await ProjectKit.ProblemAsync(await Create(t.Contributor.Id, t.Viewer.Id)));
    }

    /// <summary>Counts the commands whose text has this part.</summary>
    private sealed class QueryCount(string part) : DbCommandInterceptor
    {
        private int hits;

        public int Hits => Volatile.Read(ref hits);

        public void Reset() => Volatile.Write(ref hits, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(part, StringComparison.Ordinal)) Interlocked.Increment(ref hits);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
