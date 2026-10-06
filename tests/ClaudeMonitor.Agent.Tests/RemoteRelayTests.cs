using System.Net;
using System.Text.Json;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

public sealed class RemoteRelayTests : IDisposable
{
    private readonly RemoteFixture fx = new();
    private readonly RemoteRelay relay;

    public RemoteRelayTests() => relay = new RemoteRelay(fx.Home.Config, fx.Store, fx.Api, fx.Clock);

    public void Dispose() => fx.Dispose();

    private static string Json(object value) => JsonSerializer.Serialize(value, ApiClient.Json);

    [Fact]
    public async Task A_new_request_is_sent_as_written_and_its_answer_is_stored()
    {
        fx.Store.AddRequest("l1", "machines", "GET", "api/agent/machines", null, fx.Clock.GetUtcNow());
        fx.Store.AddRequest("l2", "grant", "POST", "api/agent/grants", """{"cwd":"/srv/app"}""", fx.Clock.GetUtcNow().AddSeconds(1));
        fx.Fake.On("GET /api/agent/machines", HttpStatusCode.OK, """[{"hostname":"h1"}]""");
        fx.Fake.On("POST /api/agent/grants", HttpStatusCode.OK, """{"id":"g1"}""");

        await relay.SendRequestsAsync(CancellationToken.None);

        var first = fx.Store.Request("l1")!;
        Assert.Equal((LocalStore.RequestStates.Done, """[{"hostname":"h1"}]""", null), (first.State, first.Result, first.Error));
        Assert.Equal("""{"id":"g1"}""", fx.Store.Request("l2")!.Result);
        Assert.Equal("""{"cwd":"/srv/app"}""", fx.Fake.Seen.Single(s => s.Path == "/api/agent/grants").Body);
        Assert.Empty(fx.Store.NewRequests(10));
    }

    [Fact]
    public async Task An_answer_with_no_content_is_stored_as_done_with_no_result()
    {
        fx.Store.AddRequest("l1", "cancel", "POST", "api/agent/runs/x/cancel", null, fx.Clock.GetUtcNow());
        fx.Fake.On("POST /api/agent/runs/x/cancel", HttpStatusCode.NoContent, "");
        await relay.SendRequestsAsync(CancellationToken.None);
        Assert.Equal((LocalStore.RequestStates.Done, null), (fx.Store.Request("l1")!.State, fx.Store.Request("l1")!.Result));
    }

    [Fact]
    public async Task A_run_request_starts_following_the_run_it_created()
    {
        var id = Guid.NewGuid();
        fx.Store.AddRequest("l1", RemoteRelay.RunKind, "POST", "api/agent/runs", "{}", fx.Clock.GetUtcNow());
        fx.Fake.On("POST /api/agent/runs", HttpStatusCode.OK, new RunCreated(id, RunStatuses.PendingApproval, null, fx.Clock.GetUtcNow().AddMinutes(15)));

        await relay.SendRequestsAsync(CancellationToken.None);

        var followed = fx.Store.Followed(id.ToString())!;
        Assert.Equal((RunStatuses.PendingApproval, false, -1), (followed.Status, followed.Done, followed.NextSeq));
        Assert.Equal(id.ToString(), Assert.Single(fx.Store.RunsToFetch(fx.Clock.GetUtcNow().AddDays(-1), 10)).RunId); // new: dirty at once
    }

    [Fact]
    public async Task Another_kind_of_request_does_not_follow_anything()
    {
        fx.Store.AddRequest("l1", "grant", "POST", "api/agent/grants", "{}", fx.Clock.GetUtcNow());
        fx.Fake.On("POST /api/agent/grants", HttpStatusCode.OK, new RunCreated(Guid.NewGuid(), RunStatuses.Approved, null, fx.Clock.GetUtcNow()));
        await relay.SendRequestsAsync(CancellationToken.None);
        Assert.Empty(fx.Store.RunsToFetch(fx.Clock.GetUtcNow().AddDays(-1), 10));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, """{"title":"target_busy","status":409}""", "target_busy")]
    [InlineData(HttpStatusCode.BadRequest, """{"title":"One or more validation errors occurred.","errors":{"Argv":["argv is required"]}}""", "One or more validation errors occurred.")]
    [InlineData(HttpStatusCode.BadRequest, """{"errors":{"Argv":["argv is required"],"Cwd":["x"]}}""", "argv is required")]
    [InlineData(HttpStatusCode.BadRequest, """{"title":"","errors":{"Cwd":["cwd is bad"]}}""", "cwd is bad")]
    [InlineData(HttpStatusCode.BadRequest, """{"errors":{"Cwd":[]}}""", "400")]
    [InlineData(HttpStatusCode.NotFound, "", RemoteRelay.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, """{"title":"machine_not_found"}""", "machine_not_found")]
    [InlineData(HttpStatusCode.NotFound, "{}", "not_found")]
    [InlineData(HttpStatusCode.BadGateway, "<html>oops</html>", "502")]
    public void An_error_answer_maps_to_its_problem_title_then_its_first_validation_error_then_its_status(HttpStatusCode status, string body, string expected) =>
        Assert.Equal(expected, RemoteRelay.ErrorCode(new ApiException(status, body)));

    [Fact]
    public void A_title_of_more_than_one_hundred_characters_is_not_used_as_a_code()
    {
        var title = new string('t', 101);
        Assert.Equal("409", RemoteRelay.ErrorCode(new ApiException(HttpStatusCode.Conflict, $$"""{"title":"{{title}}"}""")));
        var longest = new string('t', 100);
        Assert.Equal(longest, RemoteRelay.ErrorCode(new ApiException(HttpStatusCode.Conflict, $$"""{"title":"{{longest}}"}""")));
    }

    [Fact]
    public async Task An_api_refusal_fails_the_request_with_the_code_and_the_pass_goes_on_to_the_next_one()
    {
        fx.Store.AddRequest("l1", "run", "POST", "api/agent/runs", "{}", fx.Clock.GetUtcNow());
        fx.Store.AddRequest("l2", "machines", "GET", "api/agent/machines", null, fx.Clock.GetUtcNow().AddSeconds(1));
        fx.Fake.On("POST /api/agent/runs", HttpStatusCode.Conflict, """{"title":"remote_runs_disabled"}""");
        fx.Fake.On("GET /api/agent/machines", HttpStatusCode.OK, "[]");

        await relay.SendRequestsAsync(CancellationToken.None);

        Assert.Equal((LocalStore.RequestStates.Failed, "remote_runs_disabled"), (fx.Store.Request("l1")!.State, fx.Store.Request("l1")!.Error));
        Assert.Equal(LocalStore.RequestStates.Done, fx.Store.Request("l2")!.State);
    }

    [Fact]
    public async Task A_network_failure_keeps_the_request_until_it_is_two_minutes_old_and_then_fails_it_as_offline()
    {
        fx.Store.AddRequest("l1", "machines", "GET", "api/agent/machines", null, fx.Clock.GetUtcNow());
        fx.Fake.On("GET /api/agent/machines", _ => throw new HttpRequestException("no route"));

        await Assert.ThrowsAsync<HttpRequestException>(() => relay.SendRequestsAsync(CancellationToken.None));
        Assert.Equal(LocalStore.RequestStates.New, fx.Store.Request("l1")!.State);

        fx.Clock.Advance(RemoteRelay.GiveUpAfter);
        await Assert.ThrowsAsync<HttpRequestException>(() => relay.SendRequestsAsync(CancellationToken.None));
        Assert.Equal(LocalStore.RequestStates.New, fx.Store.Request("l1")!.State); // exactly two minutes: not yet

        fx.Clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<HttpRequestException>(() => relay.SendRequestsAsync(CancellationToken.None));
        Assert.Equal((LocalStore.RequestStates.Failed, RemoteRelay.Offline), (fx.Store.Request("l1")!.State, fx.Store.Request("l1")!.Error));
    }

    [Fact]
    public async Task A_followed_run_is_fetched_with_its_output_paged_until_the_run_is_finished_and_all_output_is_in()
    {
        var id = Guid.NewGuid();
        fx.Store.FollowRun(id.ToString(), RunStatuses.Approved, fx.Clock.GetUtcNow());
        fx.Fake.On($"GET /api/agent/runs/{id}", HttpStatusCode.OK, """{"status":"succeeded","exitCode":0}""");
        var calls = 0;
        fx.Fake.On($"GET /api/agent/runs/{id}/output", _ => ++calls == 1
            ? (HttpStatusCode.OK, Json(new RunOutputPage(Enumerable.Range(0, 50).Select(i => new RunOutputChunk(i, RunStreams.Stdout, $"c{i}\n")).ToList(), 50, false)))
            : (HttpStatusCode.OK, Json(new RunOutputPage([new RunOutputChunk(50, RunStreams.Stderr, "tail", true), new RunOutputChunk(51, RunStreams.Stdout, "end")], 52, true))));

        await relay.FollowRunsAsync(CancellationToken.None);

        var followed = fx.Store.Followed(id.ToString())!;
        Assert.Equal((RunStatuses.Succeeded, 52, true), (followed.Status, followed.NextSeq, followed.Done));
        Assert.Contains("\"exitCode\":0", followed.View, StringComparison.Ordinal);
        var stored = fx.Store.RunOutput(id.ToString());
        Assert.Equal(52, stored.Count);
        Assert.True(stored[50].Gap);
        Assert.Equal((RunStreams.Stderr, "tail"), (stored[50].Stream, stored[50].Body));
        Assert.Equal(2, calls);
        Assert.Empty(fx.Store.RunsToFetch(fx.Clock.GetUtcNow().AddDays(-1), 10)); // done runs are not fetched again
    }

    [Theory]
    [InlineData("succeeded", false, false)] // finished, but output still coming
    [InlineData("running", true, false)] // output complete so far, run not finished
    [InlineData("running", false, false)]
    [InlineData("timed_out", true, true)]
    public async Task A_run_is_done_only_when_its_status_is_final_and_its_output_is_complete(string status, bool outputDone, bool done)
    {
        var id = Guid.NewGuid();
        fx.Store.FollowRun(id.ToString(), RunStatuses.Approved, fx.Clock.GetUtcNow());
        fx.Fake.On($"GET /api/agent/runs/{id}", HttpStatusCode.OK, $$"""{"status":"{{status}}"}""");
        fx.Fake.On($"GET /api/agent/runs/{id}/output", HttpStatusCode.OK, new RunOutputPage([new RunOutputChunk(0, RunStreams.Stdout, "x")], 1, outputDone));

        await relay.FollowRunsAsync(CancellationToken.None);

        Assert.Equal((status, done), (fx.Store.Followed(id.ToString())!.Status, fx.Store.Followed(id.ToString())!.Done));
    }

    [Fact]
    public async Task A_followed_run_the_api_no_longer_knows_is_given_up_with_what_was_known()
    {
        var id = Guid.NewGuid();
        fx.Store.FollowRun(id.ToString(), RunStatuses.Running, fx.Clock.GetUtcNow());
        fx.Fake.On($"GET /api/agent/runs/{id}", HttpStatusCode.NotFound, "");

        await relay.FollowRunsAsync(CancellationToken.None);

        var followed = fx.Store.Followed(id.ToString())!;
        Assert.Equal((RunStatuses.Running, true), (followed.Status, followed.Done));
    }

    [Fact]
    public async Task Run_once_sends_follows_and_forgets_answered_requests_older_than_a_day_only()
    {
        var now = fx.Clock.GetUtcNow();
        fx.Store.AddRequest("old", "machines", "GET", "api/agent/machines", null, now.AddDays(-3));
        fx.Store.RequestAnswered("old", LocalStore.RequestStates.Done, "[]", null, now - RemoteRelay.ForgetAfter - TimeSpan.FromMinutes(1));
        fx.Store.AddRequest("recent", "machines", "GET", "api/agent/machines", null, now.AddDays(-3));
        fx.Store.RequestAnswered("recent", LocalStore.RequestStates.Done, "[]", null, now - RemoteRelay.ForgetAfter + TimeSpan.FromMinutes(1));
        fx.Store.AddRequest("waiting", "machines", "GET", "api/agent/machines", null, now.AddDays(-3)); // unanswered: never forgotten
        fx.Fake.On("GET /api/agent/machines", HttpStatusCode.OK, "[]");

        await relay.RunOnceAsync(CancellationToken.None);

        Assert.Null(fx.Store.Request("old"));
        Assert.NotNull(fx.Store.Request("recent"));
        Assert.Equal(LocalStore.RequestStates.Done, fx.Store.Request("waiting")!.State); // it was sent by this pass
    }

    [Fact]
    public async Task An_answer_that_is_not_json_fails_that_request_as_bad_answer_and_does_not_block_the_next()
    {
        fx.Store.AddRequest("l1", "machines", "GET", "api/agent/machines", null, fx.Clock.GetUtcNow());
        fx.Store.AddRequest("l2", "grants", "GET", "api/agent/grants", null, fx.Clock.GetUtcNow().AddSeconds(1));
        fx.Fake.On("GET /api/agent/machines", HttpStatusCode.OK, "<html>a proxy page</html>");
        fx.Fake.On("GET /api/agent/grants", HttpStatusCode.OK, "[]");

        await relay.SendRequestsAsync(CancellationToken.None);

        Assert.Equal((LocalStore.RequestStates.Failed, RemoteRelay.BadAnswer), (fx.Store.Request("l1")!.State, fx.Store.Request("l1")!.Error));
        Assert.Equal(LocalStore.RequestStates.Done, fx.Store.Request("l2")!.State);
    }

    [Fact]
    public async Task Run_once_forgets_finished_runs_older_than_the_local_retention_and_keeps_newer_ones()
    {
        var now = fx.Clock.GetUtcNow();
        foreach (var (id, fetched) in new[] { ("old", now - fx.Home.Config.RemoteLocalRetention - TimeSpan.FromHours(1)), ("recent", now - fx.Home.Config.RemoteLocalRetention + TimeSpan.FromHours(1)) })
        {
            fx.Store.FollowRun(id, RunStatuses.Succeeded, now);
            fx.Store.RunFetched(id, RunStatuses.Succeeded, "{}", 1, true, fetched);
            fx.Store.AddRunOutput(id, [new StoredChunk(id, 0, "stdout", "x", false)]);
        }

        await relay.RunOnceAsync(CancellationToken.None);

        Assert.Null(fx.Store.Followed("old"));
        Assert.Empty(fx.Store.RunOutput("old"));
        Assert.NotNull(fx.Store.Followed("recent"));
        Assert.Equal(TimeSpan.FromDays(7), fx.Home.Config.RemoteLocalRetention);
    }
}
