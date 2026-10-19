using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Starting and stopping Claude from the web (ADR-0013): the machine owner's opt-in, the environment gate, closed fields, and what a start tells the page.</summary>
[Collection(ApiGroup.Name)]
public sealed class ClaudeControlTests(ApiFactory api)
{
    private const string Project = "git:host/shop";

    /// <summary>The owner's dev machine with Claude on it, remote work on and the opt-in given: everything a start needs.</summary>
    private async Task<AskTeam> TeamAsync(bool optIn = true, string? environment = "dev", string? function = "development", string? claudeKind = ClaudeKinds.Native)
    {
        var team = await AskKit.TeamAsync(api, optIn: false, environment: environment, function: function, claudeKind: claudeKind);
        if (optIn) await Accept(team, true);
        return team;
    }

    private static async Task Accept(AskTeam team, bool accept) =>
        (await team.User.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/claude", new { accept })).EnsureSuccessStatusCode();

    private static Task<HttpResponseMessage> Start(AskTeam team, string? project = Project, string? mode = null, string? code = null) =>
        team.User.PostAsync($"/api/agents/{team.TargetId}/actions", new AgentActionRequest(AgentActionKinds.ClaudeStart, code, project, mode));

    private static Task<HttpResponseMessage> Stop(AskTeam team, string? sessionRef) =>
        team.User.PostAsync($"/api/agents/{team.TargetId}/actions", new AgentActionRequest(AgentActionKinds.ClaudeStop, SessionRef: sessionRef));

    /// <summary>A 400 whose validation problem names the field and the code the web translates.</summary>
    private static async Task AssertInvalidAsync(HttpResponseMessage response, string field, string code)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal(code, errors.GetProperty(field)[0].GetString());
    }

    private async Task SetAgentAsync(Guid agentId, Action<ClaudeMonitor.Api.Data.Agent> change)
    {
        await using var db = api.Db();
        var agent = await db.Agents.SingleAsync(a => a.Id == agentId);
        change(agent);
        await db.SaveChangesAsync();
    }

    // ---- the owner's switch --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_switch_is_off_until_the_owner_turns_it_on_the_machine_list_shows_it_and_each_change_is_audited()
    {
        var team = await TeamAsync(optIn: false);
        var machines = await team.User.GetJsonAsync($"/api/workspaces/{team.WorkspaceId}/machines");
        Assert.False(machines.EnumerateArray().Single(m => m.GetProperty("agentId").GetGuid() == team.TargetId).GetProperty("acceptsClaudeControl").GetBoolean());

        await Accept(team, true);

        machines = await team.User.GetJsonAsync($"/api/workspaces/{team.WorkspaceId}/machines");
        Assert.True(machines.EnumerateArray().Single(m => m.GetProperty("agentId").GetGuid() == team.TargetId).GetProperty("acceptsClaudeControl").GetBoolean());
        await Accept(team, false);
        await using var db = api.Db();
        var audit = await db.AuditEvents.AsNoTracking().Where(a => a.Action == AuditActions.AgentClaudeControlChanged && a.TargetId == team.TargetId.ToString()).OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, audit.Count);
        Assert.True(audit[0].Detail!.RootElement.GetProperty("after").GetBoolean());
        Assert.False(audit[1].Detail!.RootElement.GetProperty("after").GetBoolean());
        var row = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId);
        Assert.Equal((false, team.User.Id), (row.AcceptsClaudeControl, row.AcceptsClaudeControlSetBy));
    }

    [Fact]
    public async Task Only_the_machines_owner_may_turn_it_on_and_an_unknown_machine_is_not_found()
    {
        var team = await TeamAsync(optIn: false);
        var other = await RemoteKit.MemberAsync(api, team.User, "claude-member", "admin");

        Assert.Equal(HttpStatusCode.NotFound, (await other.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/claude", new { accept = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await team.User.SendAsync(HttpMethod.Put, $"/api/agents/{Guid.NewGuid()}/claude", new { accept = true })).StatusCode);
        await using var db = api.Db();
        Assert.False((await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId)).AcceptsClaudeControl);
    }

    [Fact]
    public async Task Turning_it_on_needs_a_recent_sign_in_and_turning_it_off_never_does()
    {
        var team = await TeamAsync(optIn: false);
        api.Clock.Advance(TimeSpan.FromMinutes(11)); // past the re-authentication window
        await RemoteKit.AssertProblemAsync(await team.User.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/claude", new { accept = true }), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        await SetAgentAsync(team.TargetId, a => a.AcceptsClaudeControl = true);

        Assert.Equal(HttpStatusCode.OK, (await team.User.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/claude", new { accept = false })).StatusCode);
    }

    [Fact]
    public async Task Turning_it_off_ends_every_open_claude_action_of_the_machine_and_leaves_the_others()
    {
        var team = await TeamAsync();
        var start = await ActionKit.CreateAsync(team.User, team.TargetId, AgentActionKinds.Status);
        var claude = (await (await Start(team)).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json))!;

        await Accept(team, false);

        Assert.Equal(AgentActionStatuses.Expired, (await ActionKit.RowAsync(api, claude.Id)).Status);
        Assert.Equal(AgentActionStatuses.Queued, (await ActionKit.RowAsync(api, start.Id)).Status);
    }

    [Fact]
    public async Task A_machine_moved_to_another_workspace_forgets_the_switch()
    {
        var team = await TeamAsync();
        var second = (await (await team.User.PostAsync("/api/workspaces", new { name = "Second" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await team.User.SendAsync(HttpMethod.Patch, $"/api/agents/{team.TargetId}", new { workspaceId = second })).StatusCode);

        await using var db = api.Db();
        Assert.False((await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId)).AcceptsClaudeControl);
    }

    // ---- a start --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "manual")]
    [InlineData("manual", "manual")]
    [InlineData("plan", "plan")]
    [InlineData("acceptEdits", "acceptEdits")]
    public async Task A_start_stores_the_project_and_the_permission_mode_defaulting_to_manual_audits_both_and_tells_the_agent(string? asked, string stored)
    {
        var team = await TeamAsync();
        using var agent = await RemoteKit.OpenAsync(team.Target.Http, "/api/agent/stream");
        await RemoteKit.NextEventAsync(agent, AgentStreamEvents.Ready);

        var response = await Start(team, Project, asked);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = (await response.Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json))!;
        Assert.Equal((AgentActionKinds.ClaudeStart, Project, stored, null), (view.Kind, view.ProjectKey, view.PermissionMode, view.SessionRef));
        var toAgent = await RemoteKit.NextEventAsync(agent, AgentStreamEvents.Action);
        Assert.Equal((AgentActionKinds.ClaudeStart, Project, stored), (toAgent.GetProperty("kind").GetString(), toAgent.GetProperty("projectKey").GetString(), toAgent.GetProperty("permissionMode").GetString()));
        await using var db = api.Db();
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == AuditActions.AgentActionRequested && a.TargetId == team.TargetId.ToString());
        var detail = audit.Detail!.RootElement;
        Assert.Equal((AgentActionKinds.ClaudeStart, Project, stored), (detail.GetProperty("kind").GetString(), detail.GetProperty("projectKey").GetString(), detail.GetProperty("permissionMode").GetString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad\nkey")]
    public async Task A_start_needs_a_project_key_without_control_characters(string? project)
    {
        var team = await TeamAsync();
        await AssertInvalidAsync(await Start(team, project), "projectKey", AgentActionErrors.InvalidProject);
        Assert.Empty(await ActionKit.RowsAsync(api, team.TargetId));
    }

    [Fact]
    public async Task A_project_key_over_the_limit_is_refused()
    {
        var team = await TeamAsync();
        await AssertInvalidAsync(await Start(team, new string('k', RunKinds.ProjectKeyMax + 1)), "projectKey", AgentActionErrors.InvalidProject);
        Assert.Equal(HttpStatusCode.OK, (await Start(team, new string('k', RunKinds.ProjectKeyMax))).StatusCode);
    }

    [Theory]
    [InlineData("bypassPermissions")]
    [InlineData("auto")]
    [InlineData("dontAsk")]
    [InlineData("MANUAL")]
    [InlineData("anything")]
    public async Task Only_the_three_careful_permission_modes_may_be_asked_for_never_bypass_auto_or_dontAsk(string mode)
    {
        var team = await TeamAsync();
        await AssertInvalidAsync(await Start(team, Project, mode), "permissionMode", AgentActionErrors.InvalidPermissionMode);
        Assert.Empty(await ActionKit.RowsAsync(api, team.TargetId));
    }

    [Fact]
    public async Task A_start_needs_a_recent_sign_in_or_a_code_like_restart_and_update()
    {
        var team = await TeamAsync();
        api.Clock.Advance(TimeSpan.FromMinutes(11));
        await RemoteKit.AssertProblemAsync(await Start(team), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        Assert.Empty(await ActionKit.RowsAsync(api, team.TargetId));
    }

    // ---- who and where --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_the_owners_switch_nothing_starts()
    {
        var team = await TeamAsync(optIn: false);
        await RemoteKit.AssertProblemAsync(await Start(team), HttpStatusCode.Conflict, AgentActionErrors.ClaudeNotAccepted);
        await RemoteKit.AssertProblemAsync(await Stop(team, "34a6ea9d"), HttpStatusCode.Conflict, AgentActionErrors.ClaudeNotAccepted);
        Assert.Empty(await ActionKit.RowsAsync(api, team.TargetId));
    }

    [Fact]
    public async Task With_remote_work_switched_off_in_the_workspace_nothing_starts()
    {
        var team = await TeamAsync();
        (await team.User.SendAsync(HttpMethod.Put, $"/api/workspaces/{team.WorkspaceId}/remote-settings", new { remoteRunsEnabled = false })).EnsureSuccessStatusCode();
        await RemoteKit.AssertProblemAsync(await Start(team), HttpStatusCode.Conflict, RemoteErrors.Disabled);
    }

    [Fact]
    public async Task Only_the_machines_owner_may_start_and_the_start_is_not_a_member_right()
    {
        var team = await TeamAsync();
        var admin = await RemoteKit.MemberAsync(api, team.User, "claude-admin", "admin");
        await RemoteKit.AssertProblemAsync(await admin.PostAsync($"/api/agents/{team.TargetId}/actions", new AgentActionRequest(AgentActionKinds.ClaudeStart, ProjectKey: Project)), HttpStatusCode.Forbidden, AgentActionErrors.OwnerOnly);
    }

    [Theory]
    [InlineData("test", "application", true)]
    [InlineData("dev", "development", true)]
    [InlineData("production", "application", false)]
    [InlineData("stage", "application", false)]
    [InlineData("preprod", "application", false)]
    [InlineData("other", "application", false)]
    [InlineData("dev", "database", false)]
    [InlineData("test", "database", false)]
    public async Task Only_a_dev_or_test_machine_that_is_not_a_database_may_be_started(string environment, string function, bool allowed)
    {
        var team = await TeamAsync(environment: environment, function: function);

        var response = await Start(team);

        if (allowed) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        else await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Conflict, AgentActionErrors.ClaudeEnvironment);
    }

    [Fact]
    public async Task A_machine_whose_environment_no_admin_has_set_is_refused_and_a_claim_of_production_makes_a_dev_machine_strict()
    {
        var team = await TeamAsync(environment: null, function: null);
        await RemoteKit.AssertProblemAsync(await Start(team), HttpStatusCode.Conflict, RemoteErrors.RoleNotSet);

        var dev = await TeamAsync();
        await SetAgentAsync(dev.TargetId, a => a.ClaimedEnvironment = MachineEnvironments.Production);
        await RemoteKit.AssertProblemAsync(await Start(dev), HttpStatusCode.Conflict, AgentActionErrors.ClaudeEnvironment);
    }

    [Fact]
    public async Task A_machine_without_Claude_Code_and_a_service_agent_are_refused()
    {
        var none = await TeamAsync(claudeKind: ClaudeKinds.None);
        await RemoteKit.AssertProblemAsync(await Start(none), HttpStatusCode.Conflict, AgentActionErrors.ClaudeNotInstalled);

        var service = await TeamAsync();
        await SetAgentAsync(service.TargetId, a => a.ServiceMode = true);
        await RemoteKit.AssertProblemAsync(await Start(service), HttpStatusCode.Conflict, AgentActionErrors.ClaudeServiceMode);
    }

    // ---- a stop ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stop_names_only_a_session_an_earlier_start_of_this_machine_returned()
    {
        var team = await TeamAsync();
        var start = (await (await Start(team)).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json))!;

        await RemoteKit.AssertProblemAsync(await Stop(team, "34a6ea9d"), HttpStatusCode.Conflict, AgentActionErrors.NotStartedHere); // the start has not reported yet
        await ActionKit.FinishAsync(team.Target, start.Id, AgentActionStatuses.Succeeded, "started");
        Assert.Null((await ActionKit.RowAsync(api, start.Id)).SessionRef); // a report without the id names none

        var second = (await (await Start(team, "git:host/other")).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json))!;
        Assert.Equal(HttpStatusCode.NoContent, (await team.Target.Http.PostAsJsonAsync($"/api/agent/actions/{second.Id}/status", new AgentActionReport(AgentActionStatuses.Delivered), TestUser.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Target.Http.PostAsJsonAsync($"/api/agent/actions/{second.Id}/status", new AgentActionReport(AgentActionStatuses.Succeeded, "started 34a6ea9d", "34a6ea9d"), TestUser.Json)).StatusCode);
        Assert.Equal("34a6ea9d", (await ActionKit.RowAsync(api, second.Id)).SessionRef);

        var stop = await Stop(team, "34a6ea9d");

        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        var view = (await stop.Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json))!;
        Assert.Equal((AgentActionKinds.ClaudeStop, "34a6ea9d"), (view.Kind, view.SessionRef));
        await RemoteKit.AssertProblemAsync(await Stop(team, "deadbeef"), HttpStatusCode.Conflict, AgentActionErrors.NotStartedHere);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("34a6ea9")]
    [InlineData("34A6EA9D")]
    [InlineData("34a6ea9dx")]
    [InlineData("--all")]
    [InlineData("zzzzzzzz")]
    public async Task A_stop_takes_only_a_short_session_id_never_an_option_or_text(string? sessionRef)
    {
        var team = await TeamAsync();
        await AssertInvalidAsync(await Stop(team, sessionRef), "sessionRef", AgentActionErrors.InvalidSessionRef);
    }

    [Fact]
    public async Task Only_a_successful_start_may_name_a_session_and_only_with_a_well_formed_id()
    {
        var team = await TeamAsync();
        var start = (await (await Start(team)).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json))!;
        Assert.Equal(HttpStatusCode.NoContent, (await ActionKit.ReportAsync(team.Target, start.Id, AgentActionStatuses.Delivered)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await team.Target.Http.PostAsJsonAsync($"/api/agent/actions/{start.Id}/status", new AgentActionReport(AgentActionStatuses.Failed, "not trusted", "34a6ea9d"), TestUser.Json)).StatusCode);

        Assert.Null((await ActionKit.RowAsync(api, start.Id)).SessionRef); // a failure names no session

        var status = await ActionKit.CreateAsync(team.User, team.TargetId, AgentActionKinds.Status);
        Assert.Equal(HttpStatusCode.NoContent, (await ActionKit.ReportAsync(team.Target, status.Id, AgentActionStatuses.Delivered)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Target.Http.PostAsJsonAsync($"/api/agent/actions/{status.Id}/status", new AgentActionReport(AgentActionStatuses.Succeeded, "ok", "34a6ea9d"), TestUser.Json)).StatusCode);
        Assert.Null((await ActionKit.RowAsync(api, status.Id)).SessionRef); // another kind names none
    }
}
