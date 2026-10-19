using System.Net;
using System.Net.Http.Json;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Asking for a fixed agent action from the web (ADR-0005, "Agent actions from the web"): who may ask, the closed set of kinds, and what is published and audited.</summary>
[Collection(ApiGroup.Name)]
public sealed class AgentActionCreateTests(ApiFactory api)
{
    private async Task<int> AuditCountAsync(Guid agentId)
    {
        await using var db = api.Db();
        return await db.AuditEvents.CountAsync(a => a.Action == "agent.action_requested" && a.TargetId == agentId.ToString());
    }

    private async Task AssertNothingStoredAsync(Guid agentId)
    {
        Assert.Empty(await ActionKit.RowsAsync(api, agentId));
        Assert.Equal(0, await AuditCountAsync(agentId));
    }

    // ---- the owner asks -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AgentActionKinds.Restart)]
    [InlineData(AgentActionKinds.UpdateCheck)]
    [InlineData(AgentActionKinds.Update)]
    [InlineData(AgentActionKinds.Status)]
    public async Task The_owner_of_a_machine_queues_each_kind_and_it_is_audited_with_the_kind_only_and_published_to_the_web_and_the_agent(string kind)
    {
        var team = await RemoteKit.TeamAsync(api);
        using var web = await RemoteKit.OpenAsync(team.Admin.Http, $"/api/workspaces/{team.WorkspaceId}/stream");
        await RemoteKit.NextEventAsync(web, "ready");
        using var agent = await RemoteKit.OpenAsync(team.Target.Http, "/api/agent/stream");
        await RemoteKit.NextEventAsync(agent, AgentStreamEvents.Ready);

        var response = await ActionKit.PostAsync(team.Owner, team.TargetId, kind);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = (await response.Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json))!;
        Assert.Equal((team.TargetId, kind, AgentActionStatuses.Queued, null, null), (view.AgentId, view.Kind, view.Status, view.Result, view.FinishedAt));
        Assert.Equal(AgentActionKinds.QueueTtl, view.ExpiresAt - view.CreatedAt);
        var row = Assert.Single(await ActionKit.RowsAsync(api, team.TargetId));
        Assert.Equal((view.Id, team.WorkspaceId, team.Owner.Id, kind, AgentActionStatuses.Queued), (row.Id, row.WorkspaceId, row.RequestedBy, row.Kind, row.Status));
        Assert.True((row.CreatedAt - view.CreatedAt).Duration() < TimeSpan.FromMilliseconds(1)); // the database keeps microseconds
        Assert.Equal(AgentActionKinds.QueueTtl, row.ExpiresAt - row.CreatedAt);

        await using var db = api.Db();
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == "agent.action_requested" && a.TargetId == team.TargetId.ToString());
        Assert.Equal((team.WorkspaceId, team.Owner.Id, "agent"), (audit.WorkspaceId, audit.ActorUserId, audit.TargetType));
        var detail = audit.Detail!.RootElement.EnumerateObject().ToList();
        Assert.Equal(kind, Assert.Single(detail).Value.GetString()); // the kind and nothing else: no result, no code
        Assert.Equal("kind", detail[0].Name);

        var onWeb = await RemoteKit.NextEventAsync(web, AgentActions.WorkspaceEvent, e => e.GetProperty("agentId").GetGuid() == team.TargetId);
        Assert.Equal((view.Id, AgentActionStatuses.Queued), (onWeb.GetProperty("id").GetGuid(), onWeb.GetProperty("status").GetString()));
        var toAgent = await RemoteKit.NextEventAsync(agent, AgentStreamEvents.Action);
        Assert.Equal((view.Id, kind), (toAgent.GetProperty("id").GetGuid(), toAgent.GetProperty("kind").GetString()));
        Assert.Equal(view.ExpiresAt, toAgent.GetProperty("expiresAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("")]
    [InlineData("format_disk")]
    [InlineData("RESTART")]
    [InlineData(" restart")]
    [InlineData("restart ")]
    [InlineData("update-check")]
    [InlineData("status; rm -rf /")]
    public async Task A_kind_outside_the_closed_set_is_refused_and_nothing_is_stored(string kind)
    {
        var team = await RemoteKit.TeamAsync(api);

        await RemoteKit.AssertInvalidAsync(await ActionKit.PostAsync(team.Owner, team.TargetId, kind), "kind", AgentActionErrors.InvalidKind);

        await AssertNothingStoredAsync(team.TargetId);
    }

    [Fact]
    public async Task A_request_with_no_kind_at_all_is_refused_and_nothing_is_stored()
    {
        var team = await RemoteKit.TeamAsync(api);

        var response = await team.Owner.PostAsync($"/api/agents/{team.TargetId}/actions", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingStoredAsync(team.TargetId);
    }

    // ---- who may ask --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Someone_who_is_not_a_member_of_the_workspace_finds_no_machine_to_ask_and_sees_no_list()
    {
        var team = await RemoteKit.TeamAsync(api);
        var stranger = await api.NewClient().SignedUpAsync("act-stranger");

        Assert.Equal(HttpStatusCode.NotFound, (await ActionKit.PostAsync(stranger, team.TargetId, AgentActionKinds.Status)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/agents/{team.TargetId}/actions")).StatusCode);
        await AssertNothingStoredAsync(team.TargetId);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("other")]
    [InlineData("viewer")]
    public async Task A_member_who_does_not_own_the_machine_is_refused_as_owner_only_whatever_their_role(string who)
    {
        var team = await RemoteKit.TeamAsync(api);
        var user = who switch
        {
            "admin" => team.Admin,
            "other" => team.Other,
            "viewer" => await RemoteKit.MemberAsync(api, team.Admin, "act-viewer", "viewer"),
            _ => throw new ArgumentOutOfRangeException(nameof(who)),
        };

        var response = await ActionKit.PostAsync(user, team.TargetId, AgentActionKinds.Status);

        await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Forbidden, AgentActionErrors.OwnerOnly);
        await AssertNothingStoredAsync(team.TargetId);
    }

    [Fact]
    public async Task An_unknown_machine_and_a_revoked_machine_are_not_found_for_their_owner()
    {
        var team = await RemoteKit.TeamAsync(api);
        var revoked = await team.Owner.ConnectAgentAsync(team.WorkspaceId);
        (await team.Owner.PostAsync($"/api/agents/{revoked.Tokens.AgentId}/revoke")).EnsureSuccessStatusCode();

        foreach (var id in new[] { Guid.NewGuid(), revoked.Tokens.AgentId })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await ActionKit.PostAsync(team.Owner, id, AgentActionKinds.Status)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await team.Owner.Http.GetAsync($"/api/agents/{id}/actions")).StatusCode);
        }

        await AssertNothingStoredAsync(revoked.Tokens.AgentId);
    }

    [Fact]
    public async Task Someone_not_signed_in_and_a_machine_acting_as_a_person_get_nothing()
    {
        var team = await RemoteKit.TeamAsync(api);
        var anonymous = api.NewClient();
        var body = new { kind = AgentActionKinds.Status };

        var asAnonymous = (await anonymous.PostAsync($"/api/agents/{team.TargetId}/actions", body)).StatusCode;
        var asAgent = (await team.Target.Http.PostAsJsonAsync($"/api/agents/{team.TargetId}/actions", body, TestUser.Json)).StatusCode;
        var readAnonymous = (await anonymous.Http.GetAsync($"/api/agents/{team.TargetId}/actions")).StatusCode;

        Assert.All(new[] { asAnonymous, asAgent, readAnonymous }, s => Assert.True(s is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"answered {s}"));
        await AssertNothingStoredAsync(team.TargetId);
    }

    // ---- the re-authentication of restart and update -------------------------------------------------------------

    [Theory]
    [InlineData(AgentActionKinds.Restart)]
    [InlineData(AgentActionKinds.Update)]
    public async Task Restart_and_update_need_a_recent_sign_in_while_status_and_update_check_do_not(string kind)
    {
        var team = await RemoteKit.TeamAsync(api);
        api.Clock.Advance(TimeSpan.FromMinutes(11)); // past the re-authentication window, well inside the session's life

        await RemoteKit.AssertProblemAsync(await ActionKit.PostAsync(team.Owner, team.TargetId, kind), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        await AssertNothingStoredAsync(team.TargetId);

        Assert.Equal(HttpStatusCode.OK, (await ActionKit.PostAsync(team.Owner, team.TargetId, AgentActionKinds.Status)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ActionKit.PostAsync(team.Owner, team.TargetId, AgentActionKinds.UpdateCheck)).StatusCode);
        Assert.Equal([AgentActionKinds.Status, AgentActionKinds.UpdateCheck], (await ActionKit.RowsAsync(api, team.TargetId)).Select(r => r.Kind).Order().ToArray());
    }

    [Theory]
    [InlineData(AgentActionKinds.Restart)]
    [InlineData(AgentActionKinds.Update)]
    public async Task A_fresh_sign_in_renews_the_right_to_restart_or_update(string kind)
    {
        var team = await RemoteKit.TeamAsync(api);
        api.Clock.Advance(TimeSpan.FromMinutes(11));
        await RemoteKit.AssertProblemAsync(await ActionKit.PostAsync(team.Owner, team.TargetId, kind), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);

        var again = api.NewClient();
        (await again.PostAsync("/api/auth/login", new { email = team.Owner.Email, password = TestUser.Password })).EnsureSuccessStatusCode();

        Assert.Equal(kind, (await ActionKit.CreateAsync(again, team.TargetId, kind)).Kind);
    }

    [Fact]
    public async Task An_old_sign_in_is_made_good_by_a_valid_two_step_code_and_not_by_a_wrong_or_missing_one()
    {
        var team = await RemoteKit.TeamAsync(api);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        api.Clock.Advance(TimeSpan.FromMinutes(11));

        await RemoteKit.AssertProblemAsync(await ActionKit.PostAsync(team.Owner, team.TargetId, AgentActionKinds.Restart), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        await RemoteKit.AssertProblemAsync(await ActionKit.PostAsync(team.Owner, team.TargetId, AgentActionKinds.Restart, "000000"), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        await AssertNothingStoredAsync(team.TargetId);

        var created = await ActionKit.CreateAsync(team.Owner, team.TargetId, AgentActionKinds.Restart, RemoteKit.TotpNow(api, secret));
        Assert.Equal(AgentActionStatuses.Queued, created.Status);
    }

    // ---- a strict machine (production or the database function) -------------------------------------------------

    [Theory]
    [InlineData("production", "application")]
    [InlineData("test", "database")]
    public async Task On_a_strict_machine_restart_and_update_need_the_owner_to_have_two_step_sign_in_but_status_and_update_check_still_work(string environment, string function)
    {
        var team = await ActionKit.StrictTeamAsync(api, environment, function);

        await RemoteKit.AssertProblemAsync(await ActionKit.PostAsync(team.Owner, team.TargetId, AgentActionKinds.Restart), HttpStatusCode.Forbidden, RemoteErrors.TotpRequired);
        await RemoteKit.AssertProblemAsync(await ActionKit.PostAsync(team.Owner, team.TargetId, AgentActionKinds.Update, "123456"), HttpStatusCode.Forbidden, RemoteErrors.TotpRequired);
        await AssertNothingStoredAsync(team.TargetId);

        Assert.Equal(AgentActionKinds.Status, (await ActionKit.CreateAsync(team.Owner, team.TargetId, AgentActionKinds.Status)).Kind);
        Assert.Equal(AgentActionKinds.UpdateCheck, (await ActionKit.CreateAsync(team.Owner, team.TargetId, AgentActionKinds.UpdateCheck)).Kind);
    }

    [Theory]
    [InlineData(AgentActionKinds.Restart)]
    [InlineData(AgentActionKinds.Update)]
    public async Task On_a_strict_machine_a_recent_sign_in_is_not_enough_and_a_valid_two_step_code_is(string kind)
    {
        var team = await ActionKit.StrictTeamAsync(api);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);

        await RemoteKit.AssertProblemAsync(await ActionKit.PostAsync(team.Owner, team.TargetId, kind), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        await RemoteKit.AssertProblemAsync(await ActionKit.PostAsync(team.Owner, team.TargetId, kind, "000000"), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        await AssertNothingStoredAsync(team.TargetId);

        var created = await ActionKit.CreateAsync(team.Owner, team.TargetId, kind, RemoteKit.TotpNow(api, secret));
        Assert.Equal((kind, AgentActionStatuses.Queued), (created.Kind, created.Status));
    }

    [Fact]
    public async Task A_machine_that_is_not_strict_restarts_on_a_recent_sign_in_alone_even_in_a_test_role()
    {
        var team = await ScriptKit.TeamAsync(api, "test", "application", ExecLevels.Argv);

        Assert.Equal(AgentActionStatuses.Queued, (await ActionKit.CreateAsync(team.Owner, team.TargetId, AgentActionKinds.Restart)).Status);
    }

    [Fact]
    public async Task The_closed_set_of_kinds_is_the_four_fixed_ones_the_two_for_Claude_and_the_five_remote_writes_and_each_fixed_one_can_be_queued_together()
    {
        Assert.Equal(new[] { "restart", "update_check", "update", "status", "claude_start", "claude_stop", "plugin_enable", "plugin_disable", "plugin_update", "plugin_install", "project_setting" }, AgentActionKinds.All.ToArray());
        var team = await RemoteKit.TeamAsync(api);
        var fixedKinds = AgentActionKinds.All.Where(k => !AgentActionKinds.IsClaude(k)).ToList();
        foreach (var kind in fixedKinds) await ActionKit.CreateAsync(team.Owner, team.TargetId, kind);
        Assert.Equal(fixedKinds.Order().ToArray(), (await ActionKit.RowsAsync(api, team.TargetId)).Select(r => r.Kind).Order().ToArray());
    }
}
