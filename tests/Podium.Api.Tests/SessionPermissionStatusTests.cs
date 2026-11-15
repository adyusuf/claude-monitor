using System.Text.Json;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>A permission request from an agent makes a running session wait, and never reopens an ended one.</summary>
[Collection(ApiGroup.Name)]
public sealed class SessionPermissionStatusTests(ApiFactory api)
{
    private DateTimeOffset T(int seconds) => api.Clock.GetUtcNow().AddSeconds(seconds);

    private static async Task<JsonElement> SessionOf(TestUser user) =>
        (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions")).GetProperty("items")[0];

    [Fact]
    public async Task A_permission_request_does_not_reopen_an_ended_session()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "ended-ask");
        await w.Agent.SendAsync(TestAgent.Hook(w.Session, "SessionEnd", new { }, T(1)));
        Assert.Equal("ended", (await SessionOf(w.Owner)).GetProperty("status").GetString());

        await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", new { command = "make build" });

        Assert.Equal("ended", (await SessionOf(w.Owner)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_permission_request_still_makes_a_running_session_wait()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "active-ask");

        await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", new { command = "make build" });

        Assert.Equal("waiting", (await SessionOf(w.Owner)).GetProperty("status").GetString());
    }
}
