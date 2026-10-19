using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The automatic mode of Claude on a machine (ADR-0014 P3): the owner's choice, when it needs a sign-in, and when the agent is told it.</summary>
[Collection(ApiGroup.Name)]
public sealed class ClaudeAutoModeTests(ApiFactory api)
{
    private async Task<AskTeam> TeamAsync(string? environment = "dev", string? function = "development")
    {
        var team = await AskKit.TeamAsync(api, optIn: false, environment: environment, function: function, claudeKind: ClaudeKinds.Native);
        return team;
    }

    private static Task<HttpResponseMessage> Put(AskTeam team, bool accept, string? auto = null, string? code = null) =>
        team.User.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/claude", new { accept, autoMode = auto, code });

    private static async Task<AgentSettings> SettingsAsync(AskTeam team) =>
        (await team.Target.Http.GetFromJsonAsync<AgentSettings>("/api/agent/settings", TestUser.Json))!;

    private static async Task<string> ModeInListAsync(AskTeam team) =>
        (await team.User.GetJsonAsync($"/api/workspaces/{team.WorkspaceId}/machines")).EnumerateArray()
            .Single(m => m.GetProperty("agentId").GetGuid() == team.TargetId).GetProperty("claudeAutoMode").GetString()!;

    [Fact]
    public async Task It_is_off_by_default_the_owner_turns_it_on_with_the_switch_and_the_agent_is_told_only_then()
    {
        var team = await TeamAsync();
        Assert.Equal(ClaudeAutoModes.Off, await ModeInListAsync(team));
        Assert.Equal(ClaudeAutoModes.Off, (await SettingsAsync(team)).ClaudeAuto);

        var view = await (await Put(team, true, ClaudeAutoModes.OnMessage)).Content.ReadFromJsonAsync<AgentClaudeView>(TestUser.Json);

        Assert.Equal((true, ClaudeAutoModes.OnMessage), (view!.Accepts, view.AutoMode));
        Assert.Equal(ClaudeAutoModes.OnMessage, await ModeInListAsync(team));
        Assert.Equal(ClaudeAutoModes.OnMessage, (await SettingsAsync(team)).ClaudeAuto);
        await using var db = api.Db();
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == AuditActions.AgentClaudeControlChanged && a.TargetId == team.TargetId.ToString());
        Assert.Equal((ClaudeAutoModes.Off, ClaudeAutoModes.OnMessage), (audit.Detail!.RootElement.GetProperty("autoBefore").GetString(), audit.Detail.RootElement.GetProperty("autoAfter").GetString()));
    }

    [Fact]
    public async Task A_request_without_a_mode_keeps_the_mode_and_turning_the_switch_off_turns_the_mode_off_too()
    {
        var team = await TeamAsync();
        (await Put(team, true, ClaudeAutoModes.OnMessage)).EnsureSuccessStatusCode();

        (await Put(team, true)).EnsureSuccessStatusCode();
        Assert.Equal(ClaudeAutoModes.OnMessage, await ModeInListAsync(team));

        (await Put(team, false)).EnsureSuccessStatusCode();
        Assert.Equal(ClaudeAutoModes.Off, await ModeInListAsync(team));
        Assert.Equal(ClaudeAutoModes.Off, (await SettingsAsync(team)).ClaudeAuto);
        (await Put(team, true)).EnsureSuccessStatusCode(); // on again: the mode does not come back by itself
        Assert.Equal(ClaudeAutoModes.Off, await ModeInListAsync(team));
    }

    [Fact]
    public async Task Turning_the_mode_on_needs_a_recent_sign_in_but_turning_it_off_never_does()
    {
        var team = await TeamAsync();
        (await Put(team, true)).EnsureSuccessStatusCode();
        api.Clock.Advance(TimeSpan.FromMinutes(11)); // past the re-authentication window

        await RemoteKit.AssertProblemAsync(await Put(team, true, ClaudeAutoModes.OnMessage), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        Assert.Equal(ClaudeAutoModes.Off, await ModeInListAsync(team));
        await using (var db = api.Db())
        {
            var row = await db.Agents.SingleAsync(a => a.Id == team.TargetId);
            row.ClaudeAutoMode = ClaudeAutoModes.OnMessage;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await Put(team, true, ClaudeAutoModes.Off)).StatusCode);
        Assert.Equal(ClaudeAutoModes.Off, await ModeInListAsync(team));
    }

    [Fact]
    public async Task A_mode_that_is_not_in_the_list_is_refused_and_nothing_changes()
    {
        var team = await TeamAsync();
        var refused = await Put(team, true, "always");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(AgentActionErrors.InvalidAutoMode, (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("autoMode")[0].GetString());
        await using var db = api.Db();
        Assert.False((await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId)).AcceptsClaudeControl);
    }

    [Theory]
    [InlineData("production", "application")]
    [InlineData("dev", "database")]
    public async Task It_cannot_be_turned_on_for_a_production_or_database_machine(string environment, string function)
    {
        var team = await TeamAsync(environment, function);
        await RemoteKit.AssertProblemAsync(await Put(team, true, ClaudeAutoModes.OnMessage), HttpStatusCode.Conflict, AgentActionErrors.ClaudeEnvironment);
        Assert.Equal(ClaudeAutoModes.Off, await ModeInListAsync(team));
    }

    [Fact]
    public async Task The_agent_hears_off_as_soon_as_a_condition_stops_holding_even_though_the_owners_choice_is_kept()
    {
        var team = await TeamAsync();
        (await Put(team, true, ClaudeAutoModes.OnMessage)).EnsureSuccessStatusCode();
        (await team.User.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/role", new { environment = "production", function = "application" })).EnsureSuccessStatusCode();

        Assert.Equal(ClaudeAutoModes.Off, (await SettingsAsync(team)).ClaudeAuto);
        await using var db = api.Db();
        Assert.Equal(ClaudeAutoModes.OnMessage, (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId)).ClaudeAutoMode);
    }

    [Fact]
    public async Task A_machine_moved_to_another_workspace_forgets_the_mode()
    {
        var team = await TeamAsync();
        (await Put(team, true, ClaudeAutoModes.OnMessage)).EnsureSuccessStatusCode();
        var second = (await (await team.User.PostAsync("/api/workspaces", new { name = "Second" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await team.User.SendAsync(HttpMethod.Patch, $"/api/agents/{team.TargetId}", new { workspaceId = second })).StatusCode);

        await using var db = api.Db();
        Assert.Equal(ClaudeAutoModes.Off, (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId)).ClaudeAutoMode);
    }
}
