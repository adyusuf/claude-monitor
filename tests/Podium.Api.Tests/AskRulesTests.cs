using System.Net;
using System.Net.Http.Json;
using Podium.Api.Remote;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>Who may ask whom (ADR-0008 §3f): the same user, the same workspace, a target that opted in, has a set non-strict role and the capability, depth one, no loop, the caps.</summary>
[Collection(ApiGroup.Name)]
public sealed class AskRulesTests(ApiFactory api)
{
    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string title) => await RemoteKit.AssertProblemAsync(response, status, title);

    [Fact]
    public async Task An_ask_between_two_machines_of_one_user_is_queued_with_its_text_and_expires_after_half_an_hour_and_the_audit_row_has_no_text()
    {
        var team = await AskKit.TeamAsync(api);
        var created = await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId, question: "Is the SECRET-QUESTION-MARKER branch green?"));

        Assert.Equal(AskStatuses.Queued, created.Status);
        await using var db = api.Db();
        var row = await db.MachineAsks.AsNoTracking().SingleAsync(a => a.Id == created.Id);
        Assert.Equal(("Is the SECRET-QUESTION-MARKER branch green?", team.TargetId, team.User.Id), (row.Question, row.TargetAgentId, row.TargetUserId));
        Assert.Equal(row.CreatedAt + TimeSpan.FromMinutes(30), row.ExpiresAt);
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == "ask.created" && a.TargetId == created.Id.ToString());
        Assert.DoesNotContain("SECRET-QUESTION-MARKER", audit.Detail!.RootElement.GetRawText());
    }

    [Fact]
    public async Task A_target_whose_owner_did_not_opt_in_takes_no_ask()
    {
        var team = await AskKit.TeamAsync(api, optIn: false);
        await AssertRefusedAsync(await AskKit.PostAsync(team.Asker, AskKit.Ask(team.TargetId)), HttpStatusCode.Conflict, AskErrors.NotAccepted);
    }

    [Fact]
    public async Task A_target_nobody_has_classified_takes_no_ask()
    {
        var team = await AskKit.TeamAsync(api, environment: null, function: null);
        await AskKit.AcceptAsync(team.User, team.TargetId, true);
        await AssertRefusedAsync(await AskKit.PostAsync(team.Asker, AskKit.Ask(team.TargetId)), HttpStatusCode.Conflict, RemoteErrors.RoleNotSet);
    }

    [Theory]
    [InlineData("production", "application")]
    [InlineData("test", "database")]
    public async Task A_strict_target_takes_no_ask(string environment, string function)
    {
        var team = await AskKit.TeamAsync(api, environment: environment, function: function);
        await AssertRefusedAsync(await AskKit.PostAsync(team.Asker, AskKit.Ask(team.TargetId)), HttpStatusCode.Conflict, AskErrors.NotAccepted);
    }

    [Fact]
    public async Task A_target_without_the_ask_capability_takes_no_ask_and_one_without_claude_says_so()
    {
        var plain = await AskKit.TeamAsync(api, capabilities: []);
        await AssertRefusedAsync(await AskKit.PostAsync(plain.Asker, AskKit.Ask(plain.TargetId)), HttpStatusCode.Conflict, AskErrors.NotAccepted);
        var none = await AskKit.TeamAsync(api, capabilities: [], claudeKind: ClaudeKinds.None);
        await AssertRefusedAsync(await AskKit.PostAsync(none.Asker, AskKit.Ask(none.TargetId)), HttpStatusCode.Conflict, AskErrors.NoClaude);
    }

    [Fact]
    public async Task A_machine_of_another_user_and_a_machine_asking_itself_are_refused()
    {
        var team = await AskKit.TeamAsync(api);
        var other = await RemoteKit.TeamAsync(api); // its Owner has a Target agent in another workspace
        await AssertRefusedAsync(await AskKit.PostAsync(team.Asker, AskKit.Ask(other.TargetId)), HttpStatusCode.Conflict, AskErrors.OtherWorkspace);
        await AssertRefusedAsync(await AskKit.PostAsync(team.Asker, AskKit.Ask(team.AskerId)), HttpStatusCode.BadRequest, AskErrors.Mismatch);
    }

    [Fact]
    public async Task A_member_who_is_not_the_owner_of_the_target_cannot_ask_it_even_in_the_same_workspace()
    {
        var rk = await RemoteKit.TeamAsync(api); // Admin has Requester, Owner has Target, same workspace
        await ProfileOptInAsync(rk.Owner, rk.Target);
        await AssertRefusedAsync(await AskKit.PostAsync(rk.Requester, AskKit.Ask(rk.TargetId)), HttpStatusCode.Conflict, AskErrors.OtherUser);
    }

    private static async Task ProfileOptInAsync(TestUser owner, TestAgent target)
    {
        await AskKit.ProfileAsync(target, [AgentCapabilities.Ask]);
        await AskKit.AcceptAsync(owner, target.Tokens.AgentId, true);
    }

    [Fact]
    public async Task An_ask_is_refused_while_remote_runs_are_off_for_the_workspace()
    {
        var team = await AskKit.TeamAsync(api);
        (await team.User.SendAsync(HttpMethod.Put, $"/api/workspaces/{team.WorkspaceId}/remote-settings", new { remoteRunsEnabled = false })).EnsureSuccessStatusCode();
        await AssertRefusedAsync(await AskKit.PostAsync(team.Asker, AskKit.Ask(team.TargetId)), HttpStatusCode.Conflict, RemoteErrors.Disabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has \0 nul")]
    public async Task An_empty_blank_or_NUL_question_is_refused(string question)
    {
        var team = await AskKit.TeamAsync(api);
        await AssertRefusedAsync(await AskKit.PostAsync(team.Asker, AskKit.Ask(team.TargetId, question: question)), HttpStatusCode.BadRequest, AskErrors.InvalidQuestion);
    }

    [Fact]
    public async Task A_question_is_at_most_eight_thousand_characters()
    {
        var team = await AskKit.TeamAsync(api);
        Assert.Equal(HttpStatusCode.OK, (await AskKit.PostAsync(team.Asker, AskKit.Ask(team.TargetId, question: new string('q', 8000)))).StatusCode);
        await AssertRefusedAsync(await AskKit.PostAsync(team.Asker, AskKit.Ask(team.TargetId, question: new string('q', 8001))), HttpStatusCode.BadRequest, AskErrors.InvalidQuestion);
    }

    [Fact]
    public async Task A_retried_create_returns_the_same_ask_and_another_question_under_the_same_key_is_a_mismatch()
    {
        var team = await AskKit.TeamAsync(api);
        var req = AskKit.Ask(team.TargetId, "same-key");
        var first = await AskKit.CreateAsync(team.Asker, req);
        Assert.Equal(first.Id, (await AskKit.CreateAsync(team.Asker, req)).Id);
        await AssertRefusedAsync(await AskKit.PostAsync(team.Asker, req with { Question = "another question" }), HttpStatusCode.Conflict, AskErrors.Mismatch);
    }

    [Fact]
    public async Task The_target_may_not_ask_back_while_an_ask_to_it_is_open_that_is_a_loop()
    {
        var team = await AskKit.TeamAsync(api);
        await AskKit.ProfileAsync(team.Asker, [AgentCapabilities.Ask]);
        await AskKit.AcceptAsync(team.User, team.AskerId, true);
        await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId));

        await AssertRefusedAsync(await AskKit.PostAsync(team.Target, AskKit.Ask(team.AskerId)), HttpStatusCode.Conflict, AskErrors.Loop);
    }

    [Fact]
    public async Task A_machine_that_received_an_ask_in_the_last_fifteen_minutes_may_not_ask_on()
    {
        var team = await AskKit.TeamAsync(api);
        var third = await team.User.ConnectAgentAsync(team.WorkspaceId);
        await AskKit.ProfileAsync(third, [AgentCapabilities.Ask]);
        (await team.User.SendAsync(HttpMethod.Put, $"/api/agents/{third.Tokens.AgentId}/role", new { environment = "test", function = "web" })).EnsureSuccessStatusCode();
        await AskKit.AcceptAsync(team.User, third.Tokens.AgentId, true);
        await AskKit.DeliveredAsync(team); // the target now has an ask delivered

        await AssertRefusedAsync(await AskKit.PostAsync(team.Target, AskKit.Ask(third.Tokens.AgentId)), HttpStatusCode.Conflict, AskErrors.Depth);
        api.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.OK, (await AskKit.PostAsync(team.Target, AskKit.Ask(third.Tokens.AgentId))).StatusCode);
    }

    [Fact]
    public async Task An_agent_asks_at_most_thirty_times_an_hour_and_a_session_five_times_in_ten_minutes()
    {
        var team = await AskKit.TeamAsync(api);
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.OK, (await AskKit.PostAsync(team.Asker, AskKit.Ask(team.TargetId, question: $"question {i}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await team.Asker.SendAsync(TestAgent.Hook("s-asker", "Stop", new { }, DateTimeOffset.UtcNow))).StatusCode);
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.OK, (await AskKit.PostAsync(team.Asker, AskKit.Ask(team.TargetId, session: "s-asker", question: $"session question {i}"))).StatusCode);
        await AssertRefusedAsync(await AskKit.PostAsync(team.Asker, AskKit.Ask(team.TargetId, session: "s-asker")), (HttpStatusCode)429, AskErrors.Rate);

        for (var i = 0; i < 20; i++) Assert.Equal(HttpStatusCode.OK, (await AskKit.PostAsync(team.Asker, AskKit.Ask(team.TargetId, question: $"more {i}"))).StatusCode);
        await AssertRefusedAsync(await AskKit.PostAsync(team.Asker, AskKit.Ask(team.TargetId, question: "one too many")), (HttpStatusCode)429, AskErrors.Rate);
        // The window slides: asks older than an hour no longer count (made old in the database; advancing the clock would also expire the agent's token).
        await using (var db = api.Db())
        {
            var old = api.Clock.GetUtcNow().AddHours(-2);
            await db.MachineAsks.Where(a => a.RequesterAgentId == team.AskerId).ExecuteUpdateAsync(s => s.SetProperty(a => a.CreatedAt, old));
        }

        Assert.Equal(HttpStatusCode.OK, (await AskKit.PostAsync(team.Asker, AskKit.Ask(team.TargetId, question: "after the hour"))).StatusCode);
    }

    [Fact]
    public async Task The_opt_in_belongs_to_the_machines_owner_and_a_workspace_admin_who_is_not_the_owner_cannot_turn_it_on()
    {
        var rk = await RemoteKit.TeamAsync(api);
        var response = await rk.Admin.SendAsync(HttpMethod.Put, $"/api/agents/{rk.TargetId}/asks", new { accept = true });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rk.Owner.SendAsync(HttpMethod.Put, $"/api/agents/{rk.TargetId}/asks", new { accept = true })).StatusCode);
    }

    [Fact]
    public async Task Turning_the_opt_in_off_cancels_every_open_ask_to_the_machine_and_the_machine_list_shows_the_switch()
    {
        var team = await AskKit.TeamAsync(api);
        var open = await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId));
        var machines = await team.User.GetJsonAsync($"/api/workspaces/{team.WorkspaceId}/machines");
        Assert.True(machines.EnumerateArray().Single(m => m.GetProperty("agentId").GetGuid() == team.TargetId).GetProperty("acceptsAsks").GetBoolean());

        await AskKit.AcceptAsync(team.User, team.TargetId, false);

        await using var db = api.Db();
        Assert.Equal(AskStatuses.Cancelled, (await db.MachineAsks.AsNoTracking().SingleAsync(a => a.Id == open.Id)).Status);
        await AssertRefusedAsync(await AskKit.PostAsync(team.Asker, AskKit.Ask(team.TargetId)), HttpStatusCode.Conflict, AskErrors.NotAccepted);
    }

    [Fact]
    public async Task Switching_remote_work_off_for_the_workspace_cancels_open_asks()
    {
        var team = await AskKit.TeamAsync(api);
        var open = await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId));
        (await team.User.SendAsync(HttpMethod.Put, $"/api/workspaces/{team.WorkspaceId}/remote-settings", new { remoteRunsEnabled = false })).EnsureSuccessStatusCode();
        await using var db = api.Db();
        Assert.Equal(AskStatuses.Cancelled, (await db.MachineAsks.AsNoTracking().SingleAsync(a => a.Id == open.Id)).Status);
    }

    [Fact]
    public async Task Moving_the_target_to_another_workspace_clears_its_opt_in_and_cancels_its_open_asks()
    {
        var team = await AskKit.TeamAsync(api);
        var open = await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId));
        var other = await team.User.PostAsync("/api/workspaces", new { name = "Second" });
        var second = (await other.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await team.User.SendAsync(HttpMethod.Patch, $"/api/agents/{team.TargetId}", new { workspaceId = second })).StatusCode);

        await using var db = api.Db();
        Assert.Equal(AskStatuses.Cancelled, (await db.MachineAsks.AsNoTracking().SingleAsync(a => a.Id == open.Id)).Status);
        Assert.False((await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId)).AcceptsAsks);
    }
}
