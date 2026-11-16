using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// F-206: a refresh answer lost after the server committed leaves the agent holding the replaced token. Within
/// <see cref="ApiConfig.AgentRefreshGrace"/> of the replace, and while its successor refresh token was never replaced, the replaced token is honoured
/// once; the unused successor then counts as replaced. Anything else stays a reuse that revokes the agent.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class RefreshGraceTests(ApiFactory api)
{
    private TimeSpan Grace => api.Services.GetRequiredService<ApiConfig>().AgentRefreshGrace;

    private Task<HttpResponseMessage> RefreshAsync(string token) =>
        api.CreateClient().PostAsJsonAsync("/api/agent/token/refresh", new RefreshRequest(token), TestUser.Json);

    private async Task<TokenResponse> RotatedAsync(string token)
    {
        var response = await RefreshAsync(token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(TestUser.Json))!;
    }

    private async Task<string?> AgentStatusAsync(Guid agentId)
    {
        await using var db = api.Db();
        return await db.Agents.AsNoTracking().Where(a => a.Id == agentId).Select(a => a.Status).FirstOrDefaultAsync();
    }

    private HttpClient AgentHttp(TokenResponse tokens)
    {
        var http = api.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        http.DefaultRequestHeaders.Add(AgentHeaders.Version, "0.3.0");
        return http;
    }

    [Fact]
    public void The_grace_window_is_two_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), Grace);
    }

    [Fact]
    public async Task A_lost_answer_is_retried_once_within_the_window_and_the_lost_pair_ends()
    {
        var user = await api.NewClient().SignedUpAsync("grace");
        var agent = await user.ConnectAgentAsync();
        var lost = await RotatedAsync(agent.Tokens.RefreshToken); // the server committed, the answer never arrived
        api.Clock.Advance(Grace);

        var retried = await RotatedAsync(agent.Tokens.RefreshToken);

        Assert.NotEqual(lost.RefreshToken, retried.RefreshToken);
        Assert.Equal(HttpStatusCode.NoContent, (await AgentHttp(retried).PostAsync("/api/agent/heartbeat", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AgentHttp(lost).PostAsync("/api/agent/heartbeat", null)).StatusCode);
        var next = await RotatedAsync(retried.RefreshToken); // the chain goes on from the retried pair
        Assert.Equal(AgentStatuses.Active, await AgentStatusAsync(agent.Tokens.AgentId));

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(agent.Tokens.RefreshToken)).StatusCode); // once only
        Assert.Equal(AgentStatuses.Revoked, await AgentStatusAsync(agent.Tokens.AgentId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(next.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task After_the_window_the_replaced_token_is_a_reuse()
    {
        var user = await api.NewClient().SignedUpAsync("grace-late");
        var agent = await user.ConnectAgentAsync();
        await RotatedAsync(agent.Tokens.RefreshToken);
        api.Clock.Advance(Grace + TimeSpan.FromSeconds(1));

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(agent.Tokens.RefreshToken)).StatusCode);
        Assert.Equal(AgentStatuses.Revoked, await AgentStatusAsync(agent.Tokens.AgentId));
    }

    [Fact]
    public async Task A_replaced_token_whose_successor_was_used_is_a_reuse_even_inside_the_window()
    {
        var user = await api.NewClient().SignedUpAsync("grace-used");
        var agent = await user.ConnectAgentAsync();
        var received = await RotatedAsync(agent.Tokens.RefreshToken);
        var onward = await RotatedAsync(received.RefreshToken); // the agent did get the answer and moved on

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(agent.Tokens.RefreshToken)).StatusCode);
        Assert.Equal(AgentStatuses.Revoked, await AgentStatusAsync(agent.Tokens.AgentId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(onward.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task A_used_successor_access_token_does_not_end_the_window_a_replay_gets_one_pair_and_the_real_agents_refresh_revokes_all()
    {
        var user = await api.NewClient().SignedUpAsync("grace-replay");
        var agent = await user.ConnectAgentAsync();
        var real = await RotatedAsync(agent.Tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.NoContent, (await AgentHttp(real).PostAsync("/api/agent/heartbeat", null)).StatusCode); // only the access token was used

        var stolen = await RotatedAsync(agent.Tokens.RefreshToken); // the documented bounded risk (docs/security.md): one valid pair

        Assert.Equal(HttpStatusCode.Unauthorized, (await AgentHttp(real).PostAsync("/api/agent/heartbeat", null)).StatusCode); // the real agent's next call fails
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(real.RefreshToken)).StatusCode); // and its refresh is a reuse
        Assert.Equal(AgentStatuses.Revoked, await AgentStatusAsync(agent.Tokens.AgentId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await AgentHttp(stolen).PostAsync("/api/agent/heartbeat", null)).StatusCode); // the replayed pair ends too
    }

    [Fact]
    public async Task The_successor_skipped_by_the_grace_is_a_reuse_when_it_shows_up_later()
    {
        var user = await api.NewClient().SignedUpAsync("grace-fork");
        var agent = await user.ConnectAgentAsync();
        var skipped = await RotatedAsync(agent.Tokens.RefreshToken);
        var retried = await RotatedAsync(agent.Tokens.RefreshToken);

        // Two holders of one chain: whoever shows the skipped successor reveals the fork, and the agent is revoked.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(skipped.RefreshToken)).StatusCode);
        Assert.Equal(AgentStatuses.Revoked, await AgentStatusAsync(agent.Tokens.AgentId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(retried.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Parallel_retries_of_a_lost_answer_get_one_pair_and_the_rest_are_a_reuse()
    {
        var user = await api.NewClient().SignedUpAsync("grace-race");
        var agent = await user.ConnectAgentAsync();
        await RotatedAsync(agent.Tokens.RefreshToken);

        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RefreshAsync(agent.Tokens.RefreshToken)));

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.All(answers.Where(a => a.StatusCode != HttpStatusCode.OK), a => Assert.Equal(HttpStatusCode.Unauthorized, a.StatusCode));
        Assert.Equal(AgentStatuses.Revoked, await AgentStatusAsync(agent.Tokens.AgentId));
        await using var db = api.Db();
        var refreshRows = await db.AgentTokens.AsNoTracking()
            .Where(t => t.AgentId == agent.Tokens.AgentId && t.Kind == AgentTokenKinds.Refresh).ToListAsync();
        Assert.Equal(3, refreshRows.Count); // the enrolment's token, the lost successor and the one grace pair
        Assert.Equal(2, refreshRows.Select(t => t.ReplacedBy).Where(id => id is not null).Distinct().Count());
    }
}
