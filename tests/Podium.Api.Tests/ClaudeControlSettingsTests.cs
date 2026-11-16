using System.Net.Http.Json;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>The agent's settings tell it the owner's opt-in to Claude control (not only inside the automatic mode), so it can check it before it acts (F-201).</summary>
[Collection(ApiGroup.Name)]
public sealed class ClaudeControlSettingsTests(ApiFactory api)
{
    private static async Task<bool?> ToldAsync(AskTeam team) =>
        (await team.Target.Http.GetFromJsonAsync<AgentSettings>("/api/agent/settings", TestUser.Json))!.ClaudeControl;

    [Fact]
    public async Task The_settings_say_the_opt_in_is_off_until_the_owner_turns_it_on_and_off_again_when_they_turn_it_off()
    {
        var team = await AskKit.TeamAsync(api, optIn: false, environment: "dev", function: "development");
        Assert.False(await ToldAsync(team));

        (await team.User.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/claude", new { accept = true })).EnsureSuccessStatusCode();
        Assert.True(await ToldAsync(team));

        (await team.User.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/claude", new { accept = false })).EnsureSuccessStatusCode();
        Assert.False(await ToldAsync(team));
    }
}
