using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>A refresh token is single use even when the same token arrives several times at once (AgentTokens.RefreshAsync); the one
/// lost-answer grace (F-206) never forks the chain.</summary>
[Collection(ApiGroup.Name)]
public sealed class RefreshRaceTests(ApiFactory api)
{
    private const int Parallel = 8;

    [Fact]
    public async Task Concurrent_refresh_with_the_same_token_issues_one_chain()
    {
        var user = await api.NewClient().SignedUpAsync("refresh-race");
        var agent = await user.ConnectAgentAsync();
        var request = new RefreshRequest(agent.Tokens.RefreshToken);

        var answers = await Task.WhenAll(Enumerable.Range(0, Parallel)
            .Select(_ => api.CreateClient().PostAsJsonAsync("/api/agent/token/refresh", request, TestUser.Json)));

        // One answer wins the replace. A loser that reads the token only after that commit looks like a lost-answer retry and may
        // get the one grace pair (F-206); every other loser is a reuse, so with this many there is always a reuse at the end.
        var issued = answers.Count(a => a.StatusCode == HttpStatusCode.OK);
        Assert.InRange(issued, 1, 2);
        Assert.All(answers.Where(a => a.StatusCode != HttpStatusCode.OK), a => Assert.Equal(HttpStatusCode.Unauthorized, a.StatusCode));
        await using var db = api.Db();
        var refreshRows = await db.AgentTokens.AsNoTracking()
            .Where(t => t.AgentId == agent.Tokens.AgentId && t.Kind == AgentTokenKinds.Refresh).ToListAsync();
        Assert.Equal(1 + issued, refreshRows.Count); // the enrolment's token and one successor per answer that issued a pair
        // One chain, never a fork: every link but the newest is replaced, each by a different row of the set.
        var links = refreshRows.Where(t => t.ReplacedBy is not null).Select(t => t.ReplacedBy!.Value).ToList();
        Assert.Equal(refreshRows.Count - 1, links.Count);
        Assert.Equal(links.Count, links.Distinct().Count());
        Assert.All(links, id => Assert.Contains(refreshRows, t => t.Id == id));
        Assert.All(refreshRows, t => Assert.NotNull(t.RevokedAt)); // the losers were judged a reuse: the whole chain is revoked
    }
}
