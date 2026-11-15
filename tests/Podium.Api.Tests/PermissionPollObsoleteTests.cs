using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// The agent's poll of one permission request has no caller in the product any more (the answer comes on the stream), but an older agent may still
/// use it, so it stays and keeps returning real values; it says it is obsolete with a <c>Deprecation</c> header (F-196, backward compatibility).
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class PermissionPollObsoleteTests(ApiFactory api)
{
    [Fact]
    public async Task The_poll_still_returns_the_status_and_the_answer_and_says_it_is_obsolete()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "poll");
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", new { command = "make build" });
        var open = await w.Agent.Http.GetAsync($"/api/agent/permission-requests/{request.Id}");
        Assert.Equal(HttpStatusCode.NoContent, (await w.Owner.PostAsync($"/api/permission-requests/{request.Id}/answer", new { decision = PermissionDecisions.Deny, reason = "not now" })).StatusCode);

        var answered = await w.Agent.Http.GetAsync($"/api/agent/permission-requests/{request.Id}");

        var openBody = await open.Content.ReadFromJsonAsync<JsonElement>();
        var body = await answered.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("open", openBody.GetProperty("status").GetString());
        Assert.Equal(("answered", "deny", "not now"), (body.GetProperty("status").GetString(), body.GetProperty("answer").GetProperty("decision").GetString(),
            body.GetProperty("answer").GetProperty("reason").GetString()));
        foreach (var response in new[] { open, answered })
        {
            Assert.Equal("true", Assert.Single(response.Headers.GetValues("Deprecation")));
        }
    }
}
