using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// A permission request carries the agent's own key (F-188): a create sent again after a lost response gets the request it made instead of a
/// second open one, also when both arrive at the same moment. A request without a key (an older agent) is made every time, as before.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class PermissionRequestKeyTests(ApiFactory api)
{
    private static Task<HttpResponseMessage> PostAsync(RuleWorld w, string? key) =>
        w.Agent.Http.PostAsJsonAsync("/api/agent/permission-requests", new PermissionRequestCreate(HarnessKinds.ClaudeCode, w.Session, "Bash",
            JsonSerializer.SerializeToElement(new { command = "make build" }), 120, ClientKey: key), TestUser.Json);

    private static async Task<PermissionRequestCreated> CreatedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PermissionRequestCreated>(TestUser.Json))!;
    }

    private async Task<int> RowsAsync(RuleWorld w)
    {
        await using var db = api.Db();
        return await db.PermissionRequests.CountAsync(p => p.AgentId == w.AgentId);
    }

    [Fact]
    public async Task The_same_key_sent_twice_gets_the_one_request_it_made()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "pk-twice");

        var first = await CreatedAsync(await PostAsync(w, "local-1"));
        var again = await CreatedAsync(await PostAsync(w, "local-1"));

        Assert.Equal((first.Id, first.ExpiresAt), (again.Id, again.ExpiresAt));
        Assert.Equal(1, await RowsAsync(w));
        Assert.Equal("local-1", (await ApprovalRuleKit.RequestRowAsync(api, first.Id)).ClientKey);
    }

    [Fact]
    public async Task Another_key_or_another_agents_same_key_makes_a_request_of_its_own()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "pk-other");
        var other = await ApprovalRuleKit.WorldAsync(api, "pk-other2");

        var first = await CreatedAsync(await PostAsync(w, "local-1"));
        var second = await CreatedAsync(await PostAsync(w, "local-2"));
        var foreign = await CreatedAsync(await PostAsync(other, "local-1"));

        Assert.Equal(3, new[] { first.Id, second.Id, foreign.Id }.Distinct().Count());
        Assert.Equal(2, await RowsAsync(w));
    }

    [Fact]
    public async Task Without_a_key_every_create_makes_a_request_as_from_an_older_agent()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "pk-none");

        var first = await CreatedAsync(await PostAsync(w, null));
        var second = await CreatedAsync(await PostAsync(w, null));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, await RowsAsync(w));
        Assert.Null((await ApprovalRuleKit.RequestRowAsync(api, first.Id)).ClientKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\u0001key")]
    public async Task A_blank_or_control_character_key_is_refused(string key)
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "pk-bad");

        var response = await PostAsync(w, key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await RowsAsync(w));
    }

    [Fact]
    public async Task The_same_key_at_the_same_moment_makes_one_request_and_both_get_its_id()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "pk-race");
        Guid sessionId;
        await using (var db = api.Db())
        {
            sessionId = await db.HarnessSessions.Where(s => s.AgentId == w.AgentId && s.ExternalId == w.Session).Select(s => s.Id).SingleAsync();
        }

        // Both read that no request has the key, then wait on the session row they both write; the second then meets the unique index.
        var responses = await RowLockHold.RunAsync(api, $"SELECT 1 FROM harness_sessions WHERE id = {sessionId} FOR UPDATE",
            () => PostAsync(w, "local-1"), () => PostAsync(w, "local-1"));

        var ids = new List<Guid>();
        foreach (var r in responses) ids.Add((await CreatedAsync(r)).Id);
        Assert.Single(ids.Distinct());
        Assert.Equal(1, await RowsAsync(w));
    }
}
