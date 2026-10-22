using System.Net;
using System.Text.Json;
using Podium.Agent.Daemon;
using Podium.Agent.Net;
using Podium.Agent.Requests;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The asks this machine sent (ADR-0008 §3f, `asks_out`): the daemon follows them like runs, and the requester layer reads the local database and never the API.</summary>
public sealed class AsksOutTests : IDisposable
{
    private readonly RemoteFixture fx = new(c => c with { RemoteWaitPoll = TimeSpan.FromMilliseconds(5) });
    private readonly RemoteRelay relay;

    public AsksOutTests() => relay = new RemoteRelay(fx.Home.Config, fx.Store, fx.Api, fx.Clock);

    public void Dispose() => fx.Dispose();

    private static string Json(object value) => JsonSerializer.Serialize(value, ApiClient.Json);

    private static AskView View(Guid id, string status, string? answer = null) =>
        new(id, Guid.NewGuid(), "web01", status, "q", answer, false, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null);

    // ---- the store --------------------------------------------------------------------------------------------

    [Fact]
    public void A_followed_ask_is_fetched_at_once_then_on_a_change_or_when_stale_and_never_after_it_is_over()
    {
        var now = fx.Clock.GetUtcNow();
        fx.Store.FollowAsk("a1", AskStatuses.Queued, now);
        Assert.Equal("a1", Assert.Single(fx.Store.AsksToFetch(now.AddDays(-1), 10)).AskId); // new: dirty

        fx.Store.AskFetched("a1", AskStatuses.Delivered, "{}", done: false, now);
        Assert.Empty(fx.Store.AsksToFetch(now.AddSeconds(-1), 10)); // fresh and not dirty
        Assert.Single(fx.Store.AsksToFetch(now.AddSeconds(1), 10)); // stale
        fx.Store.AskChanged("a1");
        Assert.Single(fx.Store.AsksToFetch(now.AddDays(-1), 10)); // changed

        fx.Store.AskFetched("a1", AskStatuses.Answered, """{"answer":"x"}""", done: true, now);
        Assert.Empty(fx.Store.AsksToFetch(now.AddDays(1), 10));
        Assert.Equal((AskStatuses.Answered, true), (fx.Store.AskOut("a1")!.Status, fx.Store.AskOut("a1")!.Done));
    }

    [Fact]
    public void Following_an_ask_twice_keeps_what_was_read_and_only_marks_it_for_a_new_fetch()
    {
        var now = fx.Clock.GetUtcNow();
        fx.Store.FollowAsk("a1", AskStatuses.Queued, now);
        fx.Store.AskFetched("a1", AskStatuses.Delivered, """{"v":1}""", false, now);
        fx.Store.FollowAsk("a1", AskStatuses.Queued, now);
        var row = fx.Store.AskOut("a1")!;
        Assert.Equal((AskStatuses.Delivered, """{"v":1}"""), (row.Status, row.View));
        Assert.Single(fx.Store.AsksToFetch(now.AddDays(-1), 10));
    }

    [Fact]
    public void Only_finished_asks_older_than_the_retention_are_forgotten_with_their_answers()
    {
        var now = fx.Clock.GetUtcNow();
        fx.Store.FollowAsk("old", AskStatuses.Queued, now);
        fx.Store.AskFetched("old", AskStatuses.Answered, "{}", true, now.AddDays(-10));
        fx.Store.FollowAsk("open", AskStatuses.Queued, now.AddDays(-10));
        fx.Store.FollowAsk("fresh", AskStatuses.Queued, now);
        fx.Store.AskFetched("fresh", AskStatuses.Answered, "{}", true, now);

        Assert.Equal(1, fx.Store.ForgetAsksOut(now.AddDays(-7)));

        Assert.Null(fx.Store.AskOut("old"));
        Assert.NotNull(fx.Store.AskOut("open"));
        Assert.NotNull(fx.Store.AskOut("fresh"));
    }

    // ---- the daemon -------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_ask_request_starts_following_the_ask_it_created()
    {
        var id = Guid.NewGuid();
        fx.Store.AddRequest("l1", RemoteRelay.AskKind, "POST", "api/agent/asks", "{}", fx.Clock.GetUtcNow());
        fx.Fake.On("POST /api/agent/asks", HttpStatusCode.OK, new AskCreated(id, AskStatuses.Queued, fx.Clock.GetUtcNow().AddMinutes(2)));

        await relay.SendRequestsAsync(CancellationToken.None);

        var followed = fx.Store.AskOut(id.ToString())!;
        Assert.Equal((AskStatuses.Queued, false), (followed.Status, followed.Done));
        Assert.Single(fx.Store.AsksToFetch(fx.Clock.GetUtcNow().AddDays(-1), 10));
    }

    [Fact]
    public async Task A_refused_ask_request_follows_nothing()
    {
        fx.Store.AddRequest("l1", RemoteRelay.AskKind, "POST", "api/agent/asks", "{}", fx.Clock.GetUtcNow());
        fx.Fake.On("POST /api/agent/asks", HttpStatusCode.Conflict, """{"title":"asks_not_accepted"}""");
        await relay.SendRequestsAsync(CancellationToken.None);
        Assert.Equal((LocalStore.RequestStates.Failed, "asks_not_accepted"), (fx.Store.Request("l1")!.State, fx.Store.Request("l1")!.Error));
        Assert.Empty(fx.Store.AsksToFetch(fx.Clock.GetUtcNow().AddDays(1), 10));
    }

    [Fact]
    public async Task A_followed_ask_is_fetched_and_kept_with_its_answer_until_it_ends()
    {
        var id = Guid.NewGuid();
        fx.Store.FollowAsk(id.ToString(), AskStatuses.Queued, fx.Clock.GetUtcNow());
        fx.Fake.On($"GET /api/agent/asks/{id}", HttpStatusCode.OK, View(id, AskStatuses.Delivered));
        await relay.FollowAsksAsync(CancellationToken.None);
        Assert.Equal((AskStatuses.Delivered, false), (fx.Store.AskOut(id.ToString())!.Status, fx.Store.AskOut(id.ToString())!.Done));

        fx.Store.AskChanged(id.ToString());
        fx.Fake.On($"GET /api/agent/asks/{id}", HttpStatusCode.OK, View(id, AskStatuses.Answered, "main"));
        await relay.FollowAsksAsync(CancellationToken.None);

        var row = fx.Store.AskOut(id.ToString())!;
        Assert.Equal((AskStatuses.Answered, true), (row.Status, row.Done));
        Assert.Contains("\"answer\":\"main\"", row.View, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AskStatuses.NoIdleSession)]
    [InlineData(AskStatuses.NotAccepting)]
    [InlineData(AskStatuses.NoAnswer)]
    [InlineData(AskStatuses.Expired)]
    [InlineData(AskStatuses.Cancelled)]
    public async Task Every_final_status_ends_the_following(string status)
    {
        var id = Guid.NewGuid();
        fx.Store.FollowAsk(id.ToString(), AskStatuses.Queued, fx.Clock.GetUtcNow());
        fx.Fake.On($"GET /api/agent/asks/{id}", HttpStatusCode.OK, View(id, status));
        await relay.FollowAsksAsync(CancellationToken.None);
        Assert.True(fx.Store.AskOut(id.ToString())!.Done);
    }

    [Fact]
    public async Task An_ask_the_api_no_longer_knows_is_given_up_with_what_was_known()
    {
        var id = Guid.NewGuid();
        fx.Store.FollowAsk(id.ToString(), AskStatuses.Delivered, fx.Clock.GetUtcNow());
        fx.Fake.On($"GET /api/agent/asks/{id}", HttpStatusCode.NotFound, "");
        await relay.FollowAsksAsync(CancellationToken.None);
        var row = fx.Store.AskOut(id.ToString())!;
        Assert.Equal((AskStatuses.Delivered, true), (row.Status, row.Done));
    }

    [Fact]
    public async Task The_run_once_pass_follows_asks_and_forgets_old_finished_ones()
    {
        var id = Guid.NewGuid();
        var old = Guid.NewGuid().ToString();
        fx.Store.FollowAsk(old, AskStatuses.Queued, fx.Clock.GetUtcNow());
        fx.Store.AskFetched(old, AskStatuses.Answered, "{}", true, fx.Clock.GetUtcNow().AddDays(-30));
        fx.Store.FollowAsk(id.ToString(), AskStatuses.Queued, fx.Clock.GetUtcNow());
        fx.Fake.On($"GET /api/agent/asks/{id}", HttpStatusCode.OK, View(id, AskStatuses.Answered, "ok"));

        await relay.RunOnceAsync(CancellationToken.None);

        Assert.Equal(AskStatuses.Answered, fx.Store.AskOut(id.ToString())!.Status);
        Assert.Null(fx.Store.AskOut(old));
    }

    // ---- the requester layer ----------------------------------------------------------------------------------

    private RemoteClient Client() => new(fx.Home.Config, fx.Clock);

    [Fact]
    public async Task Reading_a_finished_ask_returns_its_view_from_the_local_database_without_any_api_call()
    {
        var id = Guid.NewGuid();
        fx.Store.FollowAsk(id.ToString(), AskStatuses.Queued, fx.Clock.GetUtcNow());
        fx.Store.AskFetched(id.ToString(), AskStatuses.Answered, Json(View(id, AskStatuses.Answered, "main")), true, fx.Clock.GetUtcNow());

        var view = await Client().WaitAskAsync(id, 0);

        Assert.Equal(("main", AskStatuses.Answered), (view!.Answer, view.Status));
        Assert.Empty(fx.Fake.Seen);
    }

    [Fact]
    public async Task An_ask_not_followed_yet_is_followed_from_the_first_read_and_an_open_one_is_marked_for_a_fresh_fetch()
    {
        var id = Guid.NewGuid();
        Assert.Null(await Client().WaitAskAsync(id, 0)); // nothing known yet
        Assert.NotNull(fx.Store.AskOut(id.ToString()));

        fx.Store.AskFetched(id.ToString(), AskStatuses.Delivered, Json(View(id, AskStatuses.Delivered)), false, fx.Clock.GetUtcNow());
        var open = await Client().WaitAskAsync(id, 0);
        Assert.Equal(AskStatuses.Delivered, open!.Status);
        Assert.Single(fx.Store.AsksToFetch(fx.Clock.GetUtcNow().AddDays(-1), 10)); // dirty again: the daemon fetches it at the next pass
    }

    [Fact]
    public async Task The_request_key_of_an_ask_resolves_to_its_id_and_any_other_guid_is_taken_as_an_id_and_a_non_guid_is_none()
    {
        var created = new AskCreated(Guid.NewGuid(), AskStatuses.Queued, fx.Clock.GetUtcNow().AddMinutes(2));
        var key = Guid.NewGuid().ToString();
        fx.Store.AddRequest(key, RemoteRelay.AskKind, "POST", "api/agent/asks", "{}", fx.Clock.GetUtcNow());
        fx.Store.RequestAnswered(key, LocalStore.RequestStates.Done, Json(created), null, fx.Clock.GetUtcNow());

        Assert.Equal(created.Id, await Client().AskIdAsync(key));
        var other = Guid.NewGuid();
        Assert.Equal(other, await Client().AskIdAsync(other.ToString()));
        Assert.Null(await Client().AskIdAsync("not-a-guid"));
    }

    [Fact]
    public async Task The_request_key_of_a_run_resolves_to_the_run_and_a_run_this_machine_never_asked_for_is_none()
    {
        var created = new RunCreated(Guid.NewGuid(), RunStatuses.PendingApproval, null, fx.Clock.GetUtcNow().AddMinutes(15));
        var key = Guid.NewGuid().ToString();
        fx.Store.AddRequest(key, RemoteRelay.RunKind, "POST", "api/agent/runs", "{}", fx.Clock.GetUtcNow());
        fx.Store.RequestAnswered(key, LocalStore.RequestStates.Done, Json(created), null, fx.Clock.GetUtcNow());
        fx.Store.FollowRun(created.Id.ToString(), created.Status, fx.Clock.GetUtcNow());

        Assert.Equal(created.Id.ToString(), await Client().RunIdAsync(key));
        Assert.Equal(created.Id.ToString(), await Client().RunIdAsync(created.Id.ToString()));
        Assert.Null(await Client().RunIdAsync(Guid.NewGuid().ToString()));
        Assert.Null(await Client().RunIdAsync("nope"));
    }

    [Fact]
    public async Task Reading_a_finished_script_run_gives_its_output_and_the_completeness_verdict_and_an_unfinished_one_none()
    {
        var id = Guid.NewGuid().ToString();
        var view = new RunView(Guid.Parse(id), Guid.NewGuid(), "web01", RunModes.Script, null, null, null, 30, RunStatuses.Succeeded, 0, null, 6, false,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, null, OutputComplete: true, OutputReadBytes: 6);
        fx.Store.FollowRun(id, RunStatuses.Running, fx.Clock.GetUtcNow());
        fx.Store.RunFetched(id, RunStatuses.Running, Json(view with { Status = RunStatuses.Running, OutputComplete = null }), 1, false, fx.Clock.GetUtcNow());
        fx.Store.AddRunOutput(id, [new StoredChunk(id, 0, RunStreams.Stdout, "hello\n", false)]);

        var running = await Client().ReadRunAsync(id, 0);
        Assert.Null(running!.Completeness);
        Assert.Single(running.Chunks);

        fx.Store.RunFetched(id, RunStatuses.Succeeded, Json(view), 1, true, fx.Clock.GetUtcNow());
        var done = await Client().ReadRunAsync(id, 0);
        Assert.Equal("Output complete, 6 bytes.", done!.Completeness);
        Assert.Null(await Client().ReadRunAsync(Guid.NewGuid().ToString(), 0));
    }

    [Fact]
    public async Task A_stream_message_that_an_ask_changed_marks_it_for_the_next_fetch()
    {
        var id = Guid.NewGuid();
        fx.Store.FollowAsk(id.ToString(), AskStatuses.Queued, fx.Clock.GetUtcNow());
        fx.Store.AskFetched(id.ToString(), AskStatuses.Queued, "{}", false, fx.Clock.GetUtcNow());
        Assert.Empty(fx.Store.AsksToFetch(fx.Clock.GetUtcNow().AddSeconds(-1), 10));

        var stream = new Relay(fx.Home.Config, fx.Store, fx.Api, fx.Clock);
        Assert.True(await stream.OnStreamAsync(AgentStreamEvents.AskUpdate, JsonSerializer.SerializeToElement(new AskUpdateMessage(id, AskStatuses.Delivered), ApiClient.Json), CancellationToken.None));

        Assert.Single(fx.Store.AsksToFetch(fx.Clock.GetUtcNow().AddSeconds(-1), 10));
    }
}
