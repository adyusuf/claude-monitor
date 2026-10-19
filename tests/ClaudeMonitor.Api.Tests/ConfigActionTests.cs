using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The remote writes (ADR-0011 §7a): the rules of a Claude start from the web plus closed shapes for a plugin id and a project setting.</summary>
[Collection(ApiGroup.Name)]
public sealed class ConfigActionTests(ApiFactory api)
{
    private const string Project = "git:host/shop";

    private async Task<AskTeam> TeamAsync(bool optIn = true, string? environment = "dev", string? function = "development")
    {
        var team = await AskKit.TeamAsync(api, optIn: false, environment: environment, function: function, claudeKind: ClaudeKinds.Native);
        if (optIn) (await team.User.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/claude", new { accept = true })).EnsureSuccessStatusCode();
        (await team.Target.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(ExecLevels.Off, false, null, null, 2, Claude: new ClaudePresence(ClaudeKinds.Native, null, null, 1, 1, DateTimeOffset.UtcNow),
            Projects: [new ProjectFact(Project, "shop", "ab12", "main", "0123abcd", null, ProjectSources.Listed)]), TestUser.Json)).EnsureSuccessStatusCode();
        return team;
    }

    private static Task<HttpResponseMessage> Ask(AskTeam team, string kind, string? target = null, string? value = null, string? project = null, string? code = null) =>
        team.User.PostAsync($"/api/agents/{team.TargetId}/actions", new AgentActionRequest(kind, code, project, Target: target, Value: value));

    private static async Task AssertInvalidAsync(HttpResponseMessage response, string field, string code)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty(field)[0].GetString());
    }

    [Theory]
    [InlineData(AgentActionKinds.PluginEnable)]
    [InlineData(AgentActionKinds.PluginDisable)]
    [InlineData(AgentActionKinds.PluginUpdate)]
    [InlineData(AgentActionKinds.PluginInstall)]
    public async Task A_plugin_action_is_stored_with_its_id_sent_to_the_agent_and_audited(string kind)
    {
        var team = await TeamAsync();

        var created = await (await Ask(team, kind, "adyusuf@adyusuf-marketplace")).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json);

        Assert.Equal(("adyusuf@adyusuf-marketplace", null), (created!.Target, created.Value));
        await using var db = api.Db();
        Assert.Equal("adyusuf@adyusuf-marketplace", Assert.Single(await AgentActions.WaitingForAsync(db, team.TargetId, api.Clock.GetUtcNow(), CancellationToken.None)).Target);
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == AuditActions.AgentActionRequested && a.TargetId == team.TargetId.ToString());
        Assert.Equal(("adyusuf@adyusuf-marketplace", kind), (audit.Detail!.RootElement.GetProperty("target").GetString(), audit.Detail.RootElement.GetProperty("kind").GetString()));
    }

    [Theory]
    [InlineData("noat")]
    [InlineData("a@")]
    [InlineData("@m")]
    [InlineData("has space@m")]
    [InlineData("a@m; rm -rf /")]
    [InlineData("a@m@n")]
    [InlineData("--accept-command@m")]
    public async Task A_plugin_id_of_the_wrong_shape_is_refused_and_nothing_is_stored(string id)
    {
        var team = await TeamAsync();

        await AssertInvalidAsync(await Ask(team, AgentActionKinds.PluginEnable, id), "target", AgentActionErrors.InvalidPlugin);
        await AssertInvalidAsync(await Ask(team, AgentActionKinds.PluginEnable, null), "target", AgentActionErrors.InvalidPlugin);

        Assert.Empty(await ActionKit.RowsAsync(api, team.TargetId));
    }

    [Fact]
    public async Task A_plugin_action_that_carries_a_project_a_value_or_a_prompt_is_refused()
    {
        var team = await TeamAsync();
        await AssertInvalidAsync(await Ask(team, AgentActionKinds.PluginEnable, "a@m", value: "true"), "target", AgentActionErrors.InvalidPlugin);
        await AssertInvalidAsync(await Ask(team, AgentActionKinds.PluginEnable, "a@m", project: Project), "target", AgentActionErrors.InvalidPlugin);
        var withPrompt = await team.User.PostAsync($"/api/agents/{team.TargetId}/actions", new AgentActionRequest(AgentActionKinds.PluginEnable, Target: "a@m", Prompt: "do more"));
        Assert.Equal(HttpStatusCode.BadRequest, withPrompt.StatusCode);
        Assert.Empty(await ActionKit.RowsAsync(api, team.TargetId));
    }

    [Theory]
    [InlineData("mode", "C")]
    [InlineData("mode_model", "sonnet")]
    [InlineData("push.prod", "false")]
    [InlineData("run_before_push.test.unit", "true")]
    public async Task A_project_setting_for_a_reported_project_with_a_fitting_value_is_stored(string name, string value)
    {
        var team = await TeamAsync();

        var created = await (await Ask(team, AgentActionKinds.ProjectSetting, name, value, Project)).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json);

        Assert.Equal((Project, name, value), (created!.ProjectKey, created.Target, created.Value));
    }

    [Theory]
    [InlineData("mode", "Z")]
    [InlineData("mode", "")]
    [InlineData("mode_model", "opus")]
    [InlineData("push.prod", "maybe")]
    [InlineData("push.main", "true")]
    [InlineData("../x", "true")]
    [InlineData(null, "true")]
    public async Task A_setting_name_or_value_outside_the_closed_lists_is_refused(string? name, string value)
    {
        var team = await TeamAsync();
        await AssertInvalidAsync(await Ask(team, AgentActionKinds.ProjectSetting, name, value, Project), "value", AgentActionErrors.InvalidSetting);
        Assert.Empty(await ActionKit.RowsAsync(api, team.TargetId));
    }

    [Theory]
    [InlineData("git:host/unknown")]
    [InlineData("")]
    [InlineData("bad\nkey")]
    public async Task A_project_setting_for_a_project_the_machine_did_not_report_is_refused(string project)
    {
        var team = await TeamAsync();
        await AssertInvalidAsync(await Ask(team, AgentActionKinds.ProjectSetting, "mode", "B", project), "projectKey", AgentActionErrors.InvalidProject);
        Assert.Empty(await ActionKit.RowsAsync(api, team.TargetId));
    }

    [Fact]
    public async Task Every_rule_of_a_start_from_the_web_applies_the_owners_switch_the_environment_and_a_recent_sign_in()
    {
        var off = await TeamAsync(optIn: false);
        await RemoteKit.AssertProblemAsync(await Ask(off, AgentActionKinds.PluginEnable, "a@m"), HttpStatusCode.Conflict, AgentActionErrors.ClaudeNotAccepted);

        var production = await TeamAsync(environment: "production", function: "application");
        await RemoteKit.AssertProblemAsync(await Ask(production, AgentActionKinds.ProjectSetting, "mode", "B", Project), HttpStatusCode.Conflict, AgentActionErrors.ClaudeEnvironment);

        var team = await TeamAsync();
        api.Clock.Advance(TimeSpan.FromMinutes(11)); // past the re-authentication window
        await RemoteKit.AssertProblemAsync(await Ask(team, AgentActionKinds.PluginInstall, "new@m"), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        await RemoteKit.AssertProblemAsync(await Ask(team, AgentActionKinds.ProjectSetting, "mode", "B", Project), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        Assert.Empty(await ActionKit.RowsAsync(api, team.TargetId));
    }

    [Fact]
    public async Task Only_the_machines_owner_may_ask()
    {
        var team = await TeamAsync();
        var other = await RemoteKit.MemberAsync(api, team.User, "cfg-member", "admin");
        var response = await other.PostAsync($"/api/agents/{team.TargetId}/actions", new AgentActionRequest(AgentActionKinds.PluginEnable, Target: "a@m"));
        await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Forbidden, AgentActionErrors.OwnerOnly);
        Assert.Empty(await ActionKit.RowsAsync(api, team.TargetId));
    }

    [Fact]
    public async Task The_same_request_again_returns_the_open_one_and_a_different_one_finds_the_machine_busy()
    {
        var team = await TeamAsync();
        var first = await (await Ask(team, AgentActionKinds.PluginEnable, "a@m")).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json);

        var again = await (await Ask(team, AgentActionKinds.PluginEnable, "a@m")).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json);
        await RemoteKit.AssertProblemAsync(await Ask(team, AgentActionKinds.PluginEnable, "b@m"), HttpStatusCode.Conflict, AgentActionErrors.ActionBusy);

        Assert.Equal(first!.Id, again!.Id);
        Assert.Single(await ActionKit.RowsAsync(api, team.TargetId));
    }

    [Fact]
    public async Task Turning_the_switch_off_ends_a_queued_remote_write_and_a_finished_one_keeps_the_agents_words()
    {
        var team = await TeamAsync();
        var queued = await (await Ask(team, AgentActionKinds.PluginEnable, "a@m")).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json);
        var setting = await (await Ask(team, AgentActionKinds.ProjectSetting, "mode", "B", Project)).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json);
        Assert.Equal(HttpStatusCode.NoContent, (await ActionKit.ReportAsync(team.Target, setting!.Id, AgentActionStatuses.Delivered)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Target.Http.PostAsJsonAsync($"/api/agent/actions/{setting.Id}/status", new AgentActionReport(AgentActionStatuses.Succeeded, "working mode set to B"), TestUser.Json)).StatusCode);

        (await team.User.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/claude", new { accept = false })).EnsureSuccessStatusCode();

        Assert.Equal(AgentActionStatuses.Expired, (await ActionKit.RowAsync(api, queued!.Id)).Status);
        var done = await ActionKit.RowAsync(api, setting.Id);
        Assert.Equal((AgentActionStatuses.Succeeded, "working mode set to B"), (done.Status, done.Result));
    }
}
