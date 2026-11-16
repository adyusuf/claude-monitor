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

        // One answer wins the replace. A loser, whether it read the token before or after that commit, looks like a lost-answer retry and
        // may get the one grace pair (F-206, S-2); every other loser is a reuse, so with this many there is always a reuse at the end.
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

    private static Task<HttpResponseMessage> RefreshOn(HttpClient http, string token) =>
        http.PostAsJsonAsync("/api/agent/token/refresh", new RefreshRequest(token), TestUser.Json);

    private async Task<string?> AgentStatusAsync(Guid agentId)
    {
        await using var db = api.Db();
        return await db.Agents.AsNoTracking().Where(a => a.Id == agentId).Select(a => a.Status).FirstOrDefaultAsync();
    }

    [Fact]
    public async Task A_retry_that_read_the_token_before_the_first_refresh_committed_gets_the_grace_pair_and_the_agent_stays()
    {
        var user = await api.NewClient().SignedUpAsync("refresh-lost-cas");
        var agent = await user.ConnectAgentAsync();
        var fault = new CommandFault("UPDATE agent_tokens", "replaced_by");
        await using var host = HostKit.WithInterceptor(api, fault);
        // The retry has read the token (not replaced yet) and is about to replace it: the first refresh, whose answer is lost, commits now.
        HttpResponseMessage? first = null;
        fault.Before = () => first = RefreshOn(api.CreateClient(), agent.Tokens.RefreshToken).GetAwaiter().GetResult();

        var retry = await RefreshOn(host.CreateClient(), agent.Tokens.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, first?.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode); // before S-2: 401, judged a reuse, and the agent revoked
        Assert.Equal(AgentStatuses.Active, await AgentStatusAsync(agent.Tokens.AgentId));
        var pair = (await retry.Content.ReadFromJsonAsync<TokenResponse>(TestUser.Json))!;
        var lost = (await first!.Content.ReadFromJsonAsync<TokenResponse>(TestUser.Json))!;
        Assert.Equal(HttpStatusCode.OK, (await RefreshOn(api.CreateClient(), pair.RefreshToken)).StatusCode); // the chain goes on from the grace pair
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshOn(api.CreateClient(), lost.RefreshToken)).StatusCode); // the skipped successor is a reuse
        Assert.Equal(AgentStatuses.Revoked, await AgentStatusAsync(agent.Tokens.AgentId));
    }

    [Fact]
    public async Task A_retry_that_lost_the_replace_after_the_successor_was_used_is_still_a_reuse()
    {
        var user = await api.NewClient().SignedUpAsync("refresh-lost-used");
        var agent = await user.ConnectAgentAsync();
        var fault = new CommandFault("UPDATE agent_tokens", "replaced_by");
        await using var host = HostKit.WithInterceptor(api, fault);
        // A replay: the real agent refreshed and moved on with its successor before the copy's replace ran.
        fault.Before = () =>
        {
            var received = RefreshOn(api.CreateClient(), agent.Tokens.RefreshToken).GetAwaiter().GetResult();
            var tokens = received.Content.ReadFromJsonAsync<TokenResponse>(TestUser.Json).GetAwaiter().GetResult()!;
            Assert.Equal(HttpStatusCode.OK, RefreshOn(api.CreateClient(), tokens.RefreshToken).GetAwaiter().GetResult().StatusCode);
        };

        var replay = await RefreshOn(host.CreateClient(), agent.Tokens.RefreshToken);

        Assert.Null(fault.Before); // the real agent's refreshes did run inside the replay
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(AgentStatuses.Revoked, await AgentStatusAsync(agent.Tokens.AgentId));
    }
}
