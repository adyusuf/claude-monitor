using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The life of an ask (ADR-0008 §3f): delivered, answered once by the session that got it, cancelled, expired, read by its requester, retained and deleted.</summary>
[Collection(ApiGroup.Name)]
public sealed class AskLifecycleTests(ApiFactory api)
{
    private async Task<MachineAskRow> RowAsync(Guid id)
    {
        await using var db = api.Db();
        var a = await db.MachineAsks.AsNoTracking().SingleAsync(x => x.Id == id);
        return new MachineAskRow(a.Status, a.Answer, a.AnswerTruncated, a.TargetSession, a.Error, a.DeliveredAt, a.EndedAt, a.Question);
    }

    private sealed record MachineAskRow(string Status, string? Answer, bool AnswerTruncated, string? TargetSession, string? Error, DateTimeOffset? DeliveredAt, DateTimeOffset? EndedAt, string? Question);

    [Fact]
    public async Task The_target_reports_delivery_with_the_session_it_wrote_to_and_only_the_target_may()
    {
        var team = await AskKit.TeamAsync(api);
        var created = await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId));

        Assert.Equal(HttpStatusCode.NotFound, (await AskKit.StatusAsync(team.Asker, created.Id, new AskStatusUpdate(AskStatuses.Delivered, SessionExternalId: "s"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await AskKit.StatusAsync(team.Target, created.Id, new AskStatusUpdate(AskStatuses.Delivered, SessionExternalId: "s-target"))).StatusCode);

        var row = await RowAsync(created.Id);
        Assert.Equal((AskStatuses.Delivered, "s-target"), (row.Status, row.TargetSession));
        Assert.NotNull(row.DeliveredAt);
    }

    [Theory]
    [InlineData(AskStatuses.NoIdleSession)]
    [InlineData(AskStatuses.NotAccepting)]
    public async Task The_target_may_say_it_had_no_idle_session_or_is_not_accepting_and_the_ask_ends_there(string status)
    {
        var team = await AskKit.TeamAsync(api);
        var created = await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId));

        Assert.Equal(HttpStatusCode.NoContent, (await AskKit.StatusAsync(team.Target, created.Id, new AskStatusUpdate(status))).StatusCode);

        var row = await RowAsync(created.Id);
        Assert.Equal(status, row.Status);
        Assert.NotNull(row.EndedAt);
        Assert.Equal(HttpStatusCode.Conflict, (await AskKit.StatusAsync(team.Target, created.Id, new AskStatusUpdate(AskStatuses.Delivered, SessionExternalId: "s"))).StatusCode);
    }

    [Theory]
    [InlineData(AskStatuses.Answered)]
    [InlineData("nonsense")]
    [InlineData("")]
    public async Task A_status_the_target_may_not_report_is_refused(string status)
    {
        var team = await AskKit.TeamAsync(api);
        var created = await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId));
        Assert.Equal(HttpStatusCode.BadRequest, (await AskKit.StatusAsync(team.Target, created.Id, new AskStatusUpdate(status))).StatusCode);
    }

    [Fact]
    public async Task The_answer_comes_once_from_the_session_that_received_the_ask_and_the_requester_reads_it()
    {
        var team = await AskKit.TeamAsync(api);
        var created = await AskKit.DeliveredAsync(team);

        await RemoteKit.AssertProblemAsync(await AskKit.AnswerAsync(team.Target, created.Id, "main", session: "another-session"), HttpStatusCode.Conflict, AskErrors.Mismatch);
        Assert.Equal(HttpStatusCode.NoContent, (await AskKit.AnswerAsync(team.Target, created.Id, "The build is on main.")).StatusCode);
        await RemoteKit.AssertProblemAsync(await AskKit.AnswerAsync(team.Target, created.Id, "again"), HttpStatusCode.Conflict, AskErrors.NotOpen);

        var view = await team.Asker.Http.GetFromJsonAsync<AskView>($"/api/agent/asks/{created.Id}", TestUser.Json);
        Assert.Equal((AskStatuses.Answered, "The build is on main.", false), (view!.Status, view.Answer, view.AnswerTruncated));
        Assert.Equal("Which branch is the build on?", view.Question);
    }

    [Fact]
    public async Task An_answer_before_delivery_or_an_empty_one_is_refused()
    {
        var team = await AskKit.TeamAsync(api);
        var queued = await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId));
        await RemoteKit.AssertProblemAsync(await AskKit.AnswerAsync(team.Target, queued.Id, "too early"), HttpStatusCode.Conflict, AskErrors.NotOpen);
        var delivered = await AskKit.DeliveredAsync(team);
        Assert.Equal(HttpStatusCode.BadRequest, (await AskKit.AnswerAsync(team.Target, delivered.Id, "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AskKit.AnswerAsync(team.Target, delivered.Id, "has \0 nul")).StatusCode);
    }

    [Fact]
    public async Task An_answer_over_a_hundred_thousand_characters_is_cut_and_marked()
    {
        var team = await AskKit.TeamAsync(api);
        var created = await AskKit.DeliveredAsync(team);
        Assert.Equal(HttpStatusCode.NoContent, (await AskKit.AnswerAsync(team.Target, created.Id, new string('a', 100_050))).StatusCode);
        var row = await RowAsync(created.Id);
        Assert.Equal((100_000, true), (row.Answer!.Length, row.AnswerTruncated));
    }

    [Fact]
    public async Task Only_the_requesters_user_reads_an_ask_and_the_target_of_another_user_gets_not_found()
    {
        var team = await AskKit.TeamAsync(api);
        var created = await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId));
        var stranger = await api.NewClient().SignedUpAsync("ask-stranger");
        var strangerAgent = await stranger.ConnectAgentAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await strangerAgent.Http.GetAsync($"/api/agent/asks/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await strangerAgent.Http.PostAsync($"/api/agent/asks/{created.Id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/agents/{team.TargetId}/asks")).StatusCode);
    }

    [Fact]
    public async Task The_requester_cancels_an_open_ask_and_a_finished_one_cannot_be_cancelled()
    {
        var team = await AskKit.TeamAsync(api);
        var open = await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId));
        Assert.Equal(HttpStatusCode.NoContent, (await team.Asker.Http.PostAsync($"/api/agent/asks/{open.Id}/cancel", null)).StatusCode);
        Assert.Equal(AskStatuses.Cancelled, (await RowAsync(open.Id)).Status);

        var answered = await AskKit.DeliveredAsync(team);
        await AskKit.AnswerAsync(team.Target, answered.Id, "done");
        await RemoteKit.AssertProblemAsync(await team.Asker.Http.PostAsync($"/api/agent/asks/{answered.Id}/cancel", null), HttpStatusCode.Conflict, AskErrors.NotOpen);
    }

    [Fact]
    public async Task The_housekeeping_expires_a_queued_ask_after_two_minutes_and_a_delivered_one_without_an_answer_after_fifteen()
    {
        var team = await AskKit.TeamAsync(api);
        var queued = await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId, question: "queued one"));
        var delivered = await AskKit.DeliveredAsync(team, "delivered-key");
        api.Clock.Advance(TimeSpan.FromMinutes(3));
        await RemoteKit.HousekeepAsync(api);
        Assert.Equal(AskStatuses.Expired, (await RowAsync(queued.Id)).Status);
        Assert.Equal(AskStatuses.Delivered, (await RowAsync(delivered.Id)).Status);

        api.Clock.Advance(TimeSpan.FromMinutes(13));
        await RemoteKit.HousekeepAsync(api);
        Assert.Equal(AskStatuses.NoAnswer, (await RowAsync(delivered.Id)).Status);
    }

    [Fact]
    public async Task An_answer_that_arrived_first_is_not_overridden_by_the_expiry()
    {
        var team = await AskKit.TeamAsync(api);
        var created = await AskKit.DeliveredAsync(team);
        await AskKit.AnswerAsync(team.Target, created.Id, "in time");
        api.Clock.Advance(TimeSpan.FromMinutes(30));
        await RemoteKit.HousekeepAsync(api);
        Assert.Equal(AskStatuses.Answered, (await RowAsync(created.Id)).Status);
    }

    [Fact]
    public async Task A_queued_ask_is_replayed_to_the_target_on_connect_and_a_finished_one_is_not()
    {
        var team = await AskKit.TeamAsync(api);
        var queued = await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId, question: "still queued"));
        var gone = await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId, question: "cancelled one"));
        await team.Asker.Http.PostAsync($"/api/agent/asks/{gone.Id}/cancel", null);

        await using var scope = api.Services.CreateAsyncScope();
        var waiting = await AskQueries.WaitingForAsync(scope.ServiceProvider.GetRequiredService<Api.Data.MonitorDb>(), team.TargetId, api.Clock.GetUtcNow(), CancellationToken.None);

        var message = Assert.Single(waiting);
        Assert.Equal((queued.Id, "still queued"), (message.Id, message.Question));
        Assert.Matches("^[A-Za-z0-9._@-]{1,64}$", message.FromLabel);
    }

    [Fact]
    public async Task The_owner_sees_the_asks_of_their_machine_in_the_web_list_with_the_text_and_nobody_else_does()
    {
        var team = await AskKit.TeamAsync(api);
        var created = await AskKit.CreateAsync(team.Asker, AskKit.Ask(team.TargetId, question: "listed question"));
        var list = await team.User.GetJsonAsync($"/api/agents/{team.TargetId}/asks");
        var row = list.EnumerateArray().Single();
        Assert.Equal((created.Id, "listed question", AskStatuses.Queued), (row.GetProperty("id").GetGuid(), row.GetProperty("question").GetString(), row.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task A_finished_ask_is_deleted_after_the_retention_period()
    {
        var team = await AskKit.TeamAsync(api);
        var created = await AskKit.DeliveredAsync(team);
        await AskKit.AnswerAsync(team.Target, created.Id, "answer");
        api.Clock.Advance(TimeSpan.FromDays(91));
        await RemoteKit.HousekeepAsync(api);
        await using var db = api.Db();
        Assert.False(await db.MachineAsks.AnyAsync(a => a.Id == created.Id));
    }

    [Fact]
    public async Task The_export_holds_the_users_asks_and_deleting_the_account_empties_the_question_and_the_answer()
    {
        var team = await AskKit.TeamAsync(api);
        var created = await AskKit.DeliveredAsync(team);
        await AskKit.AnswerAsync(team.Target, created.Id, "private answer");

        var export = JsonDocument.Parse(await (await team.User.Http.GetAsync("/api/me/export")).Content.ReadAsStringAsync()).RootElement;
        var ask = export.GetProperty("asks").EnumerateArray().Single();
        Assert.Equal(("Which branch is the build on?", "private answer"), (ask.GetProperty("question").GetString(), ask.GetProperty("answer").GetString()));

        Assert.Equal(HttpStatusCode.NoContent, (await team.User.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).StatusCode);
        var row = await RowAsync(created.Id);
        Assert.Equal((null, null), (row.Question, row.Answer));
    }
}
