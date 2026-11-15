using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>A refresh token is single use even when the same token arrives several times at once (AgentTokens.RefreshAsync).</summary>
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

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.All(answers.Where(a => a.StatusCode != HttpStatusCode.OK), a => Assert.Equal(HttpStatusCode.Unauthorized, a.StatusCode));
        await using var db = api.Db();
        var refreshRows = await db.AgentTokens.AsNoTracking()
            .Where(t => t.AgentId == agent.Tokens.AgentId && t.Kind == AgentTokenKinds.Refresh).ToListAsync();
        Assert.Equal(2, refreshRows.Count); // the enrolment's token and exactly one successor
        var original = Assert.Single(refreshRows, t => t.ReplacedBy is not null);
        Assert.Contains(refreshRows, t => t.Id == original.ReplacedBy);
        Assert.All(refreshRows, t => Assert.NotNull(t.RevokedAt)); // the losers were judged a reuse: the whole chain is revoked
    }
}
