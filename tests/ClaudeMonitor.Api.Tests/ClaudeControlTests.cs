using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Starting and stopping Claude from the web (ADR-0014): the machine owner's opt-in, the environment gate, closed fields, and what a start tells the page.</summary>
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

    // ---- the first prompt and the trust check (ADR-0014 P2) ----------------------------------------------------------

    private static Task<HttpResponseMessage> StartWith(AskTeam team, string? prompt, string? project = Project, string? mode = null) =>
        team.User.PostAsync($"/api/agents/{team.TargetId}/actions", new AgentActionRequest(AgentActionKinds.ClaudeStart, null, project, mode, null, prompt));

    private async Task SetTrustAsync(AskTeam team, string project, bool? trusted)
    {
        await using var db = api.Db();
        db.MachineProjects.Add(new ClaudeMonitor.Api.Data.MachineProject { AgentId = team.TargetId, ProjectKey = project, WorkspaceId = team.WorkspaceId, Name = "shop", ClaudeTrusted = trusted });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_first_prompt_is_kept_for_the_agent_only_never_shown_back_and_never_audited()
    {
        var team = await TeamAsync();
        using var agent = await RemoteKit.OpenAsync(team.Target.Http, "/api/agent/stream");
        await RemoteKit.NextEventAsync(agent, AgentStreamEvents.Ready);
        const string text = "fix the failing test in the shop project";

        var response = await StartWith(team, text);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("firstPrompt", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(text, body, StringComparison.Ordinal);
        var toAgent = await RemoteKit.NextEventAsync(agent, AgentStreamEvents.Action);
        Assert.Equal(text, toAgent.GetProperty("firstPrompt").GetString());
        var id = (await ActionKit.RowsAsync(api, team.TargetId)).Single().Id;
        Assert.Equal(text, (await ActionKit.RowAsync(api, id)).FirstPrompt);
        Assert.Equal(text, Assert.Single(await ActionKit.WaitingAsync(api, team.TargetId)).FirstPrompt);
        await using var db = api.Db();
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == AuditActions.AgentActionRequested && a.TargetId == team.TargetId.ToString());
        Assert.DoesNotContain(text, audit.Detail!.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.True(audit.Detail.RootElement.GetProperty("withFirstPrompt").GetBoolean());
        var listed = await ActionKit.ListAsync(team.User, team.TargetId);
        Assert.DoesNotContain(text, string.Join("", listed.Select(l => l.GetRawText())), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_prompt_is_forgotten_the_moment_the_agent_takes_the_action()
    {
        var team = await TeamAsync();
        var id = (await (await StartWith(team, "do the thing")).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json))!.Id;

        Assert.Equal(HttpStatusCode.NoContent, (await ActionKit.ReportAsync(team.Target, id, AgentActionStatuses.Delivered)).StatusCode);

        Assert.Null((await ActionKit.RowAsync(api, id)).FirstPrompt);
    }

    [Fact]
    public async Task A_prompt_nobody_took_is_forgotten_when_the_action_expires()
    {
        var team = await TeamAsync();
        var id = (await (await StartWith(team, "do the thing")).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json))!.Id;

        api.Clock.Advance(AgentActionKinds.QueueTtl + TimeSpan.FromSeconds(1));
        _ = await ActionKit.ListAsync(team.User, team.TargetId); // any look at the machine's actions expires the stale ones

        var row = await ActionKit.RowAsync(api, id);
        Assert.Equal((AgentActionStatuses.Expired, null), (row.Status, row.FirstPrompt));
    }

    [Fact]
    public async Task Turning_the_switch_off_forgets_the_prompt_of_the_start_it_ends()
    {
        var team = await TeamAsync();
        var id = (await (await StartWith(team, "do the thing")).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json))!.Id;

        await Accept(team, false);

        Assert.Null((await ActionKit.RowAsync(api, id)).FirstPrompt);
    }

    [Theory]
    [InlineData("bad\u0000text")]
    [InlineData("escape \u001b[31m")]
    public async Task A_first_prompt_with_control_characters_is_refused(string prompt)
    {
        var team = await TeamAsync();
        await AssertInvalidAsync(await StartWith(team, prompt), "firstPrompt", AgentActionErrors.InvalidFirstPrompt);
        Assert.Empty(await ActionKit.RowsAsync(api, team.TargetId));
    }

    [Fact]
    public async Task A_first_prompt_is_at_most_2000_characters_new_lines_and_tabs_are_text_and_an_empty_one_means_none()
    {
        var team = await TeamAsync();
        await AssertInvalidAsync(await StartWith(team, new string('x', AgentActionKinds.ClaudeFirstPromptMax + 1)), "firstPrompt", AgentActionErrors.InvalidFirstPrompt);
        Assert.Empty(await ActionKit.RowsAsync(api, team.TargetId));

        var ok = await StartWith(team, "line one\n\tline two" + new string('x', AgentActionKinds.ClaudeFirstPromptMax - 19));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var id = (await ok.Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json))!.Id;
        await ActionKit.FinishAsync(team.Target, id, AgentActionStatuses.Failed, "x");

        var blank = await StartWith(team, "   \n ");
        Assert.Equal(HttpStatusCode.OK, blank.StatusCode);
        Assert.Null((await ActionKit.RowAsync(api, (await blank.Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json))!.Id)).FirstPrompt);
    }

    [Fact]
    public async Task A_first_prompt_belongs_to_a_start_only_a_stop_carries_none()
    {
        var team = await TeamAsync();
        var start = (await (await Start(team)).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json))!;
        await ActionKit.FinishAsync(team.Target, start.Id, AgentActionStatuses.Succeeded);
        await using (var db = api.Db())
        {
            var row = await db.AgentActions.SingleAsync(a => a.Id == start.Id);
            row.SessionRef = "34a6ea9d";
            await db.SaveChangesAsync();
        }

        var stop = await team.User.PostAsync($"/api/agents/{team.TargetId}/actions", new AgentActionRequest(AgentActionKinds.ClaudeStop, null, null, null, "34a6ea9d", "ignored"));

        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        Assert.Null((await ActionKit.RowAsync(api, (await stop.Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json))!.Id)).FirstPrompt);
    }

    [Fact]
    public async Task A_folder_the_agent_read_as_not_trusted_by_claude_code_is_refused_before_anything_is_queued()
    {
        var team = await TeamAsync();
        await SetTrustAsync(team, Project, false);

        await RemoteKit.AssertProblemAsync(await Start(team), HttpStatusCode.Conflict, AgentActionErrors.FolderNotTrusted);
        await RemoteKit.AssertProblemAsync(await StartWith(team, "hello"), HttpStatusCode.Conflict, AgentActionErrors.FolderNotTrusted);
        Assert.Empty(await ActionKit.RowsAsync(api, team.TargetId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task A_trusted_folder_and_one_the_agent_could_not_read_are_left_to_claude_code(bool? trusted)
    {
        var team = await TeamAsync();
        await SetTrustAsync(team, Project, trusted);

        Assert.Equal(HttpStatusCode.OK, (await Start(team)).StatusCode);
    }

    [Fact]
    public async Task What_the_agent_reports_about_trust_is_stored_per_project_and_shown_on_the_machine_page()
    {
        var team = await TeamAsync();
        await using (var db = api.Db())
        {
            var facts = new[] { new ProjectFact(Project, "shop", null, null, null, null, ProjectSources.Listed, Trusted: false), new ProjectFact("git:host/blog", "blog", null, null, null, null, ProjectSources.Listed, Trusted: true), new ProjectFact("git:host/old", "old", null, null, null, null, ProjectSources.Listed) };
            await MachineProjects.ApplyAsync(db, team.TargetId, team.WorkspaceId, facts, api.Clock.GetUtcNow(), CancellationToken.None);
            await db.SaveChangesAsync();
        }

        var detail = await team.User.GetJsonAsync($"/api/agents/{team.TargetId}/detail");
        var trusted = detail.GetProperty("projects").EnumerateArray().ToDictionary(p => p.GetProperty("key").GetString()!, p => p.TryGetProperty("trusted", out var t) && t.ValueKind != JsonValueKind.Null ? t.GetBoolean() : (bool?)null);
        Assert.Equal((false, true, (bool?)null), (trusted[Project], trusted["git:host/blog"], trusted["git:host/old"]));
    }

    [Fact]
    public async Task A_second_start_that_asks_for_something_else_is_refused_while_one_is_open_but_the_same_click_twice_gets_the_open_one()
    {
        var team = await TeamAsync();
        var first = (await (await Start(team, Project, "plan")).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json))!;

        var again = (await (await Start(team, Project, "plan")).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json))!;
        Assert.Equal(first.Id, again.Id);

        await RemoteKit.AssertProblemAsync(await Start(team, "git:host/other", "plan"), HttpStatusCode.Conflict, AgentActionErrors.ClaudeStartPending);
        await RemoteKit.AssertProblemAsync(await Start(team, Project, "manual"), HttpStatusCode.Conflict, AgentActionErrors.ClaudeStartPending);
        await RemoteKit.AssertProblemAsync(await StartWith(team, "a prompt", Project, "plan"), HttpStatusCode.Conflict, AgentActionErrors.ClaudeStartPending);
        Assert.Single(await ActionKit.RowsAsync(api, team.TargetId));
    }

    // ---- the automatic mode (ADR-0014 P3) --------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> SetAuto(AskTeam team, string? mode, bool accept = true, string? code = null) =>
        team.User.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/claude", new { accept, autoMode = mode, code });

    private static async Task<string?> AutoOfAsync(AskTeam team)
    {
        var machines = await team.User.GetJsonAsync($"/api/workspaces/{team.WorkspaceId}/machines");
        var machine = machines.EnumerateArray().Single(m => m.GetProperty("agentId").GetGuid() == team.TargetId);
        return machine.TryGetProperty("claudeAutoMode", out var mode) && mode.ValueKind == JsonValueKind.String ? mode.GetString() : null;
    }

    private static async Task<string?> AutoToldToAgentAsync(AskTeam team) =>
        (await team.Target.Http.GetFromJsonAsync<AgentSettings>("/api/agent/settings", TestUser.Json))!.ClaudeAutoMode;

    [Fact]
    public async Task The_automatic_mode_is_off_until_the_owner_turns_it_on_and_then_the_page_the_list_and_the_agent_all_say_so()
    {
        var team = await TeamAsync();
        Assert.Null(await AutoOfAsync(team));
        Assert.Equal(ClaudeAutoModes.Off, await AutoToldToAgentAsync(team));

        var response = await SetAuto(team, ClaudeAutoModes.OnMessage);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ClaudeAutoModes.OnMessage, (await response.Content.ReadFromJsonAsync<AgentClaudeView>(TestUser.Json))!.AutoMode);
        Assert.Equal(ClaudeAutoModes.OnMessage, await AutoOfAsync(team));
        Assert.Equal(ClaudeAutoModes.OnMessage, await AutoToldToAgentAsync(team));
        await using var db = api.Db();
        var audit = await db.AuditEvents.AsNoTracking().Where(a => a.Action == AuditActions.AgentClaudeControlChanged && a.TargetId == team.TargetId.ToString()).OrderBy(a => a.Id).LastAsync();
        Assert.Equal((ClaudeAutoModes.Off, ClaudeAutoModes.OnMessage), (audit.Detail!.RootElement.GetProperty("autoBefore").GetString(), audit.Detail.RootElement.GetProperty("autoAfter").GetString()));
    }

    [Fact]
    public async Task Leaving_the_automatic_mode_out_keeps_it_as_it_is_and_off_turns_it_off()
    {
        var team = await TeamAsync();
        await SetAuto(team, ClaudeAutoModes.OnMessage);

        await SetAuto(team, null);
        Assert.Equal(ClaudeAutoModes.OnMessage, await AutoOfAsync(team));

        await SetAuto(team, ClaudeAutoModes.Off);
        Assert.Null(await AutoOfAsync(team));
    }

    [Fact]
    public async Task Turning_the_switch_off_turns_the_automatic_mode_off_with_it()
    {
        var team = await TeamAsync();
        await SetAuto(team, ClaudeAutoModes.OnMessage);

        await Accept(team, false);

        Assert.Null(await AutoOfAsync(team));
        Assert.Equal(ClaudeAutoModes.Off, await AutoToldToAgentAsync(team));
        await Accept(team, true);
        Assert.Null(await AutoOfAsync(team)); // turning the switch back on does not bring it back
    }

    [Theory]
    [InlineData("sometimes")]
    [InlineData("ON_MESSAGE")]
    [InlineData("")]
    public async Task Only_the_three_known_modes_are_accepted(string mode)
    {
        var team = await TeamAsync();
        await AssertInvalidAsync(await SetAuto(team, mode), "autoMode", AgentActionErrors.InvalidAutoMode);
        Assert.Null(await AutoOfAsync(team));
    }

    [Fact]
    public async Task Turning_it_on_needs_a_recent_sign_in_but_turning_it_off_does_not()
    {
        var team = await TeamAsync();
        api.Clock.Advance(TimeSpan.FromMinutes(11));

        await RemoteKit.AssertProblemAsync(await SetAuto(team, ClaudeAutoModes.OnMessage), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        Assert.Null(await AutoOfAsync(team));

        await SetAgentAsync(team.TargetId, a => a.ClaudeAutoMode = ClaudeAutoModes.OnMessage);
        Assert.Equal(HttpStatusCode.OK, (await SetAuto(team, ClaudeAutoModes.Off)).StatusCode);
    }

    [Theory]
    [InlineData("production", "application")]
    [InlineData(null, null)]
    public async Task It_cannot_be_turned_on_for_a_machine_a_start_would_be_refused_on(string? environment, string? function)
    {
        var team = await TeamAsync(optIn: false, environment: environment, function: function);
        await SetAgentAsync(team.TargetId, a => a.AcceptsClaudeControl = true);

        var response = await SetAuto(team, ClaudeAutoModes.OnMessage);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(await AutoOfAsync(team));
    }

    [Fact]
    public async Task It_cannot_be_turned_on_for_a_service_agent_or_without_remote_work_and_the_agent_is_never_told_it_then()
    {
        var team = await TeamAsync();
        await SetAgentAsync(team.TargetId, a => a.ServiceMode = true);
        Assert.Equal(HttpStatusCode.Conflict, (await SetAuto(team, ClaudeAutoModes.OnMessage)).StatusCode);

        await SetAgentAsync(team.TargetId, a => { a.ServiceMode = false; a.ClaudeAutoMode = ClaudeAutoModes.OnMessage; });
        await using (var db = api.Db())
        {
            await db.WorkspaceSettings.Where(s => s.WorkspaceId == team.WorkspaceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.RemoteRunsEnabled, false));
        }

        Assert.Equal(ClaudeAutoModes.Off, await AutoToldToAgentAsync(team)); // stored on, told off: the workspace switch comes first
    }

    [Fact]
    public async Task Only_the_owner_sets_it_and_a_machine_moved_to_another_workspace_forgets_it()
    {
        var team = await TeamAsync();
        var other = await RemoteKit.MemberAsync(api, team.User, "auto-member", "admin");
        Assert.Equal(HttpStatusCode.NotFound, (await other.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/claude", new { accept = true, autoMode = ClaudeAutoModes.OnMessage })).StatusCode);

        await SetAuto(team, ClaudeAutoModes.OnMessage);
        var second = (await (await team.User.PostAsync("/api/workspaces", new { name = "Second" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await team.User.SendAsync(HttpMethod.Patch, $"/api/agents/{team.TargetId}", new { workspaceId = second })).StatusCode);

        await using var db = api.Db();
        Assert.Null((await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId)).ClaudeAutoMode);
    }

    private static Task<HttpResponseMessage> SetAlways(AskTeam team, string? project, string? code = null) =>
        team.User.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/claude", new { accept = true, autoMode = ClaudeAutoModes.Always, autoProject = project, code });

    private static async Task<string?> AutoProjectOfAsync(AskTeam team)
    {
        var machines = await team.User.GetJsonAsync($"/api/workspaces/{team.WorkspaceId}/machines");
        var machine = machines.EnumerateArray().Single(m => m.GetProperty("agentId").GetGuid() == team.TargetId);
        return machine.TryGetProperty("claudeAutoProject", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    [Fact]
    public async Task The_mode_always_needs_a_project_the_machine_has_reported_and_then_the_list_and_the_agent_both_say_which()
    {
        var team = await TeamAsync();
        await AssertInvalidAsync(await SetAlways(team, null), "autoProject", AgentActionErrors.InvalidAutoProject);
        await AssertInvalidAsync(await SetAlways(team, Project), "autoProject", AgentActionErrors.InvalidAutoProject); // never reported
        await SetTrustAsync(team, Project, true);

        var response = await SetAlways(team, Project);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = (await response.Content.ReadFromJsonAsync<AgentClaudeView>(TestUser.Json))!;
        Assert.Equal((ClaudeAutoModes.Always, Project), (view.AutoMode, view.AutoProject));
        Assert.Equal((ClaudeAutoModes.Always, Project), (await AutoOfAsync(team), await AutoProjectOfAsync(team)));
        var told = (await team.Target.Http.GetFromJsonAsync<AgentSettings>("/api/agent/settings", TestUser.Json))!;
        Assert.Equal((ClaudeAutoModes.Always, Project), (told.ClaudeAutoMode, told.ClaudeAutoProject));
    }

    [Fact]
    public async Task A_project_Claude_code_does_not_trust_cannot_be_the_one_always_keeps_a_session_in()
    {
        var team = await TeamAsync();
        await SetTrustAsync(team, Project, false);

        await AssertInvalidAsync(await SetAlways(team, Project), "autoProject", AgentActionErrors.InvalidAutoProject);
        Assert.Null(await AutoOfAsync(team));
    }

    [Fact]
    public async Task Switching_to_always_or_to_another_project_needs_a_recent_sign_in_and_leaving_it_forgets_the_project()
    {
        var team = await TeamAsync();
        await SetTrustAsync(team, Project, true);
        await SetTrustAsync(team, "git:host/blog", true);
        await SetAlways(team, Project);
        api.Clock.Advance(TimeSpan.FromMinutes(11));

        await RemoteKit.AssertProblemAsync(await SetAlways(team, "git:host/blog"), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        Assert.Equal(Project, await AutoProjectOfAsync(team));

        Assert.Equal(HttpStatusCode.OK, (await SetAuto(team, ClaudeAutoModes.OnMessage)).StatusCode); // down to a lesser mode: no sign-in
        Assert.Null(await AutoProjectOfAsync(team));
        Assert.Equal(ClaudeAutoModes.OnMessage, await AutoToldToAgentAsync(team));
    }

    [Fact]
    public async Task Turning_the_switch_off_or_moving_the_machine_forgets_the_always_project_too()
    {
        var team = await TeamAsync();
        await SetTrustAsync(team, Project, true);
        await SetAlways(team, Project);

        await Accept(team, false);

        Assert.Null(await AutoProjectOfAsync(team));
        await using var db = api.Db();
        Assert.Null((await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId)).ClaudeAutoProject);
    }

    [Fact]
    public async Task The_database_refuses_a_mode_outside_the_three()
    {
        var team = await TeamAsync();
        await Assert.ThrowsAsync<DbUpdateException>(() => SetAgentAsync(team.TargetId, a => a.ClaudeAutoMode = "sometimes"));
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
