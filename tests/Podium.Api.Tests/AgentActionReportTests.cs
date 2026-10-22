using System.Net;
using System.Net.Http.Json;
using Podium.Api.Remote;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>The agent's reports on an action (compare-and-set at every step) and the replay of waiting actions when its stream opens (ADR-0005, "Agent actions from the web").</summary>
[Collection(ApiGroup.Name)]
public sealed class AgentActionReportTests(ApiFactory api)
{
    private async Task<(RemoteTeam Team, AgentActionView Action)> QueuedAsync(string kind = AgentActionKinds.Status)
    {
        var team = await RemoteKit.TeamAsync(api);
        return (team, await ActionKit.CreateAsync(team.Owner, team.TargetId, kind));
    }

    // ---- the lifecycle ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_action_is_taken_with_delivered_then_finished_and_each_step_is_stored_and_published_to_the_web()
    {
        var (team, action) = await QueuedAsync();
        using var web = await RemoteKit.OpenAsync(team.Admin.Http, $"/api/workspaces/{team.WorkspaceId}/stream");
        await RemoteKit.NextEventAsync(web, "ready");

        Assert.Equal(HttpStatusCode.NoContent, (await ActionKit.ReportAsync(team.Target, action.Id, AgentActionStatuses.Delivered, "ignored while taking")).StatusCode);
        var taken = await ActionKit.RowAsync(api, action.Id);
        Assert.Equal((AgentActionStatuses.Delivered, null, null), (taken.Status, taken.Result, taken.FinishedAt));
        Assert.NotNull(taken.DeliveredAt);
        var first = await RemoteKit.NextEventAsync(web, AgentActions.WorkspaceEvent, e => e.GetProperty("id").GetGuid() == action.Id);
        Assert.Equal((team.TargetId, AgentActionStatuses.Delivered), (first.GetProperty("agentId").GetGuid(), first.GetProperty("status").GetString()));

        Assert.Equal(HttpStatusCode.NoContent, (await ActionKit.ReportAsync(team.Target, action.Id, AgentActionStatuses.Succeeded, "machine is fine")).StatusCode);
        var done = await ActionKit.RowAsync(api, action.Id);
        Assert.Equal((AgentActionStatuses.Succeeded, "machine is fine"), (done.Status, done.Result));
        Assert.NotNull(done.FinishedAt);
        var last = await RemoteKit.NextEventAsync(web, AgentActions.WorkspaceEvent, e => e.GetProperty("id").GetGuid() == action.Id && e.GetProperty("status").GetString() == AgentActionStatuses.Succeeded);
        Assert.Equal(team.TargetId, last.GetProperty("agentId").GetGuid());
    }

    [Fact]
    public async Task A_failed_report_finishes_a_delivered_action_as_failed_with_its_text()
    {
        var (team, action) = await QueuedAsync(AgentActionKinds.UpdateCheck);

        await ActionKit.FinishAsync(team.Target, action.Id, AgentActionStatuses.Failed, "update refused: bad-signature: no");

        var row = await ActionKit.RowAsync(api, action.Id);
        Assert.Equal((AgentActionStatuses.Failed, "update refused: bad-signature: no"), (row.Status, row.Result));
        Assert.NotNull(row.FinishedAt);
    }

    [Fact]
    public async Task A_replayed_stream_message_cannot_take_an_action_twice()
    {
        var (team, action) = await QueuedAsync(AgentActionKinds.Restart);
        Assert.Equal(HttpStatusCode.NoContent, (await ActionKit.ReportAsync(team.Target, action.Id, AgentActionStatuses.Delivered)).StatusCode);
        var taken = await ActionKit.RowAsync(api, action.Id);
        api.Clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(HttpStatusCode.Conflict, (await ActionKit.ReportAsync(team.Target, action.Id, AgentActionStatuses.Delivered)).StatusCode);

        var after = await ActionKit.RowAsync(api, action.Id);
        Assert.Equal((taken.Status, taken.DeliveredAt), (after.Status, after.DeliveredAt));
    }

    [Theory]
    [InlineData(AgentActionStatuses.Succeeded, AgentActionStatuses.Failed)]
    [InlineData(AgentActionStatuses.Succeeded, AgentActionStatuses.Succeeded)]
    [InlineData(AgentActionStatuses.Succeeded, AgentActionStatuses.Delivered)]
    [InlineData(AgentActionStatuses.Failed, AgentActionStatuses.Succeeded)]
    [InlineData(AgentActionStatuses.Failed, AgentActionStatuses.Failed)]
    [InlineData(AgentActionStatuses.Failed, AgentActionStatuses.Delivered)]
    public async Task A_final_status_never_changes(string final, string later)
    {
        var (team, action) = await QueuedAsync();
        await ActionKit.FinishAsync(team.Target, action.Id, final, "the first word");
        var before = await ActionKit.RowAsync(api, action.Id);

        Assert.Equal(HttpStatusCode.Conflict, (await ActionKit.ReportAsync(team.Target, action.Id, later, "a second word")).StatusCode);

        var after = await ActionKit.RowAsync(api, action.Id);
        Assert.Equal((final, "the first word", before.FinishedAt), (after.Status, after.Result, after.FinishedAt));
    }

    [Theory]
    [InlineData(AgentActionStatuses.Succeeded)]
    [InlineData(AgentActionStatuses.Failed)]
    public async Task An_action_cannot_be_finished_before_it_was_taken(string status)
    {
        var (team, action) = await QueuedAsync();

        Assert.Equal(HttpStatusCode.Conflict, (await ActionKit.ReportAsync(team.Target, action.Id, status, "skipped the first step")).StatusCode);

        var row = await ActionKit.RowAsync(api, action.Id);
        Assert.Equal((AgentActionStatuses.Queued, null, null), (row.Status, row.Result, row.FinishedAt));
    }

    [Fact]
    public async Task An_action_whose_queue_time_has_run_out_cannot_be_taken_but_one_just_inside_it_can()
    {
        var team = await RemoteKit.TeamAsync(api);
        var inside = await ActionKit.CreateAsync(team.Owner, team.TargetId, AgentActionKinds.Status);
        var outside = await ActionKit.CreateAsync(team.Owner, team.TargetId, AgentActionKinds.UpdateCheck);
        api.Clock.Advance(AgentActionKinds.QueueTtl - TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.NoContent, (await ActionKit.ReportAsync(team.Target, inside.Id, AgentActionStatuses.Delivered)).StatusCode);

        api.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Conflict, (await ActionKit.ReportAsync(team.Target, outside.Id, AgentActionStatuses.Delivered)).StatusCode);

        var late = await ActionKit.RowAsync(api, outside.Id);
        Assert.Null(late.DeliveredAt);
        Assert.NotEqual(AgentActionStatuses.Delivered, late.Status);
    }

    [Fact]
    public async Task A_slow_action_taken_late_in_its_queue_time_still_has_the_full_time_to_finish()
    {
        var team = await RemoteKit.TeamAsync(api);
        var action = await ActionKit.CreateAsync(team.Owner, team.TargetId, AgentActionKinds.UpdateCheck);
        api.Clock.Advance(AgentActionKinds.QueueTtl - TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.NoContent, (await ActionKit.ReportAsync(team.Target, action.Id, AgentActionStatuses.Delivered)).StatusCode);

        api.Clock.Advance(AgentActionKinds.QueueTtl - TimeSpan.FromSeconds(1)); // well past the creation time + the queue time
        await ActionKit.ListAsync(team.Owner, team.TargetId); // a read that expires what is stale must not touch this one

        Assert.Equal(HttpStatusCode.NoContent, (await ActionKit.ReportAsync(team.Target, action.Id, AgentActionStatuses.Succeeded, "up to date")).StatusCode);
        Assert.Equal((AgentActionStatuses.Succeeded, "up to date"), ((await ActionKit.RowAsync(api, action.Id)).Status, (await ActionKit.RowAsync(api, action.Id)).Result));
    }

    // ---- what is refused outright --------------------------------------------------------------------------------

    [Theory]
    [InlineData(AgentActionStatuses.Queued)]
    [InlineData(AgentActionStatuses.Expired)]
    [InlineData("cancelled")]
    [InlineData("SUCCEEDED")]
    [InlineData("bogus")]
    [InlineData("")]
    public async Task A_status_an_agent_may_not_report_is_refused_and_the_action_stays_as_it_was(string status)
    {
        var (team, action) = await QueuedAsync();

        await RemoteKit.AssertInvalidAsync(await ActionKit.ReportAsync(team.Target, action.Id, status), "status", "invalid_status");

        Assert.Equal(AgentActionStatuses.Queued, (await ActionKit.RowAsync(api, action.Id)).Status);
    }

    [Fact]
    public async Task A_report_with_no_status_is_refused()
    {
        var (team, action) = await QueuedAsync();

        var response = await team.Target.Http.PostAsJsonAsync($"/api/agent/actions/{action.Id}/status", new { result = "x" }, TestUser.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AgentActionStatuses.Queued, (await ActionKit.RowAsync(api, action.Id)).Status);
    }

    [Fact]
    public async Task Another_machines_report_is_not_found_whether_it_is_in_the_same_workspace_or_another_and_so_is_an_unknown_id()
    {
        var (team, action) = await QueuedAsync();
        var stranger = await api.NewClient().SignedUpAsync("act-report-stranger");
        var foreign = await stranger.ConnectAgentAsync();

        foreach (var agent in new[] { team.Requester, foreign })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await ActionKit.ReportAsync(agent, action.Id, AgentActionStatuses.Delivered)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await ActionKit.ReportAsync(team.Target, Guid.NewGuid(), AgentActionStatuses.Delivered)).StatusCode);
        var row = await ActionKit.RowAsync(api, action.Id);
        Assert.Equal((AgentActionStatuses.Queued, null), (row.Status, row.DeliveredAt));
    }

    [Fact]
    public async Task A_report_without_the_agents_token_or_from_a_signed_in_person_is_not_accepted()
    {
        var (team, action) = await QueuedAsync();

        var anonymous = await api.CreateClient().PostAsJsonAsync($"/api/agent/actions/{action.Id}/status", new AgentActionReport(AgentActionStatuses.Delivered), TestUser.Json);
        var person = await team.Owner.PostAsync($"/api/agent/actions/{action.Id}/status", new AgentActionReport(AgentActionStatuses.Delivered));

        Assert.All(new[] { anonymous.StatusCode, person.StatusCode }, s => Assert.True(s is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"answered {s}"));
        Assert.Equal(AgentActionStatuses.Queued, (await ActionKit.RowAsync(api, action.Id)).Status);
    }

    // ---- the result text ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2500, 2000)]
    [InlineData(2001, 2000)]
    [InlineData(2000, 2000)]
    [InlineData(1999, 1999)]
    public async Task A_result_is_kept_up_to_two_thousand_characters(int sent, int kept)
    {
        var (team, action) = await QueuedAsync();
        await ActionKit.ReportAsync(team.Target, action.Id, AgentActionStatuses.Delivered);

        Assert.Equal(HttpStatusCode.NoContent, (await ActionKit.ReportAsync(team.Target, action.Id, AgentActionStatuses.Succeeded, new string('r', sent))).StatusCode);

        var row = await ActionKit.RowAsync(api, action.Id);
        Assert.Equal(new string('r', kept), row.Result);
    }

    [Fact]
    public async Task A_finish_with_no_text_keeps_no_text()
    {
        var (team, action) = await QueuedAsync();

        await ActionKit.FinishAsync(team.Target, action.Id, AgentActionStatuses.Succeeded, null);

        Assert.Null((await ActionKit.RowAsync(api, action.Id)).Result);
    }

    [Fact]
    public async Task The_result_comes_back_to_the_owner_as_the_text_the_agent_sent()
    {
        var (team, action) = await QueuedAsync();
        const string Text = "<b>bold</b> & \"quoted\" line\nnext line";
        await ActionKit.FinishAsync(team.Target, action.Id, AgentActionStatuses.Succeeded, Text);

        var row = Assert.Single(await ActionKit.ListAsync(team.Owner, team.TargetId));

        Assert.Equal(Text, row.GetProperty("result").GetString());
    }

    // ---- the replay when the agent's stream opens ------------------------------------------------------------------

    private async Task<(RemoteTeam Team, Guid Failed, Guid Succeeded, Guid Delivered, Guid RestartQueued, Guid StatusQueued)> MixedAsync()
    {
        var team = await RemoteKit.TeamAsync(api);
        var failed = await ActionKit.CreateAsync(team.Owner, team.TargetId, AgentActionKinds.Status);
        await ActionKit.FinishAsync(team.Target, failed.Id, AgentActionStatuses.Failed);
        var succeeded = await ActionKit.CreateAsync(team.Owner, team.TargetId, AgentActionKinds.Update);
        await ActionKit.FinishAsync(team.Target, succeeded.Id);
        var delivered = await ActionKit.CreateAsync(team.Owner, team.TargetId, AgentActionKinds.UpdateCheck);
        await ActionKit.ReportAsync(team.Target, delivered.Id, AgentActionStatuses.Delivered);
        api.Clock.Advance(TimeSpan.FromSeconds(1));
        var restart = await ActionKit.CreateAsync(team.Owner, team.TargetId, AgentActionKinds.Restart);
        api.Clock.Advance(TimeSpan.FromSeconds(1));
        var status = await ActionKit.CreateAsync(team.Owner, team.TargetId, AgentActionKinds.Status);
        return (team, failed.Id, succeeded.Id, delivered.Id, restart.Id, status.Id);
    }

    [Fact]
    public async Task The_replay_holds_the_queued_actions_oldest_first_and_not_the_delivered_or_finished_ones()
    {
        var m = await MixedAsync();
        await ActionKit.InsertAsync(api, m.Team, AgentActionKinds.Update, AgentActionStatuses.Expired, api.Clock.GetUtcNow().AddHours(-1), api.Clock.GetUtcNow().AddMinutes(-50));

        var waiting = await ActionKit.WaitingAsync(api, m.Team.TargetId);

        Assert.Equal(new[] { m.RestartQueued, m.StatusQueued }, waiting.Select(w => w.Id).ToArray());
        Assert.Equal(new[] { AgentActionKinds.Restart, AgentActionKinds.Status }, waiting.Select(w => w.Kind).ToArray());
        Assert.All(waiting, w => Assert.True(w.ExpiresAt > api.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task The_replay_leaves_out_an_action_whose_queue_time_ran_out_and_marks_it_expired()
    {
        var m = await MixedAsync();
        api.Clock.Advance(AgentActionKinds.QueueTtl);

        Assert.Empty(await ActionKit.WaitingAsync(api, m.Team.TargetId));

        Assert.Equal(AgentActionStatuses.Expired, (await ActionKit.RowAsync(api, m.RestartQueued)).Status);
        Assert.Equal(AgentActionStatuses.Expired, (await ActionKit.RowAsync(api, m.StatusQueued)).Status);
    }

    [Fact]
    public async Task The_replay_of_one_machine_does_not_hold_the_actions_of_another()
    {
        var m = await MixedAsync();
        var second = await m.Team.Owner.ConnectAgentAsync(m.Team.WorkspaceId);
        await ActionKit.CreateAsync(m.Team.Owner, second.Tokens.AgentId, AgentActionKinds.Status);

        var waiting = await ActionKit.WaitingAsync(api, m.Team.TargetId);

        Assert.Equal(new[] { m.RestartQueued, m.StatusQueued }, waiting.Select(w => w.Id).ToArray());
    }

    [Fact]
    public async Task An_agent_opening_its_stream_is_sent_the_queued_actions_and_only_those_before_what_comes_live()
    {
        var m = await MixedAsync();
        using var stream = await RemoteKit.OpenAsync(m.Team.Target.Http, "/api/agent/stream");
        var live = await ActionKit.CreateAsync(m.Team.Owner, m.Team.TargetId, AgentActionKinds.Update); // finished before: queued again, so it comes after the replay
        var seen = new List<(Guid Id, string Kind)>();

        await RemoteKit.NextEventAsync(stream, AgentStreamEvents.Action, e =>
        {
            seen.Add((e.GetProperty("id").GetGuid(), e.GetProperty("kind").GetString()!));
            return e.GetProperty("id").GetGuid() == live.Id;
        });

        Assert.Equal(new[] { (m.RestartQueued, AgentActionKinds.Restart), (m.StatusQueued, AgentActionKinds.Status), (live.Id, AgentActionKinds.Update) }, seen.ToArray());
        Assert.DoesNotContain(seen, s => s.Id == m.Failed || s.Id == m.Succeeded || s.Id == m.Delivered);
    }
}
