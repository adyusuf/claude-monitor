using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>ADR-0013: the plugin an agent runs under is told with its profile, kept only when it is a plugin id, and shown to the workspace so the old plugin can be retired when none is left.</summary>
[Collection(ApiGroup.Name)]
public sealed class PluginIdTests(ApiFactory api)
{
    private static Task<HttpResponseMessage> ProfileAsync(AskTeam team, string? plugin) =>
        team.Target.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(ExecLevels.Off, false, null, null, 2, PluginId: plugin), TestUser.Json);

    private static async Task<string?> ListedAsync(AskTeam team)
    {
        var machines = await team.User.GetJsonAsync($"/api/workspaces/{team.WorkspaceId}/machines");
        var machine = machines.EnumerateArray().Single(m => m.GetProperty("agentId").GetGuid() == team.TargetId);
        return machine.TryGetProperty("pluginId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
    }

    [Theory]
    [InlineData("aipod@aipod-releases")]
    [InlineData("aipod@monitor-agent-releases")]
    [InlineData("monitor-agent@monitor-agent-releases")]
    public async Task A_plugin_id_is_stored_and_the_machine_list_shows_it(string id)
    {
        var team = await AskKit.TeamAsync(api);

        (await ProfileAsync(team, id)).EnsureSuccessStatusCode();

        Assert.Equal(id, await ListedAsync(team));
        await using var db = api.Db();
        Assert.Equal(id, (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId)).PluginId);
    }

    [Theory]
    [InlineData("not a plugin id")]
    [InlineData("a@")]
    [InlineData("@b")]
    [InlineData("UPPER@case")]
    [InlineData("x@y z")]
    [InlineData("<script>@x")]
    public async Task Anything_that_is_not_a_plugin_id_is_not_kept(string id)
    {
        var team = await AskKit.TeamAsync(api);
        (await ProfileAsync(team, "aipod@aipod-releases")).EnsureSuccessStatusCode();

        (await ProfileAsync(team, id)).EnsureSuccessStatusCode();

        Assert.Null(await ListedAsync(team));
    }

    [Fact]
    public async Task An_agent_that_reports_no_plugin_any_more_forgets_it_and_a_too_long_id_is_refused()
    {
        var team = await AskKit.TeamAsync(api);
        (await ProfileAsync(team, "aipod@aipod-releases")).EnsureSuccessStatusCode();
        (await ProfileAsync(team, null)).EnsureSuccessStatusCode();
        Assert.Null(await ListedAsync(team));

        (await ProfileAsync(team, new string('a', 70) + "@" + new string('b', 70))).EnsureSuccessStatusCode();
        Assert.Null(await ListedAsync(team));
    }

    [Theory]
    [InlineData("aipod@aipod-releases", true)]
    [InlineData("aipod@", false)]
    [InlineData("@aipod-releases", false)]
    [InlineData(null, false)]
    public void The_helper_tells_a_valid_id_from_an_invalid_one(string? id, bool valid) => Assert.Equal(valid, PluginIds.IsValid(id));
}
