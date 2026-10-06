using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;
using Microsoft.Data.Sqlite;

namespace ClaudeMonitor.Agent.Tests;

public sealed class LocalStoreRemoteTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private readonly TempHome home = new();
    private readonly LocalStore store;

    public LocalStoreRemoteTests() => store = new LocalStore(home.Config.DatabasePath);

    public void Dispose()
    {
        store.Dispose();
        home.Dispose();
    }

    // ---- runs executed -----------------------------------------------------------------------------------------

    [Fact]
    public void A_run_is_recorded_once_and_a_second_begin_says_it_is_known()
    {
        Assert.True(store.ExecBegin("r1", T0));
        Assert.False(store.ExecBegin("r1", T0.AddMinutes(1)));
        Assert.True(store.ExecBegin("r2", T0));
        var row = store.ExecRunOf("r1")!;
        Assert.Equal((LocalStore.ExecStates.Running, null, false), (row.State, row.FinalStatus, row.Reported));
        Assert.Equal(["r1", "r2"], store.ExecRunsIn(LocalStore.ExecStates.Running).Select(r => r.RunId).Order());
    }

    [Fact]
    public void Ending_a_run_keeps_the_resolved_program_when_none_is_given_and_makes_it_unreported_again()
    {
        store.ExecBegin("r1", T0);
        store.ExecEnd("r1", RunStatuses.Succeeded, 0, null, false, "/usr/bin/true");
        store.ExecReported("r1");
        Assert.Empty(store.UnreportedExecs());

        store.ExecEnd("r1", RunStatuses.Failed, 7, "oops", true, null);

        var row = store.ExecRunOf("r1")!;
        Assert.Equal((LocalStore.ExecStates.Finished, RunStatuses.Failed, 7, "oops", true, "/usr/bin/true", false),
            (row.State, row.FinalStatus, row.ExitCode, row.Error, row.Truncated, row.ResolvedExe, row.Reported));
        Assert.Equal("r1", Assert.Single(store.UnreportedExecs()).RunId);
        Assert.Empty(store.ExecRunsIn(LocalStore.ExecStates.Running));
    }

    [Fact]
    public void Only_finished_runs_that_were_not_yet_reported_are_waiting_for_a_report()
    {
        store.ExecBegin("running", T0);
        store.ExecBegin("done", T0);
        store.ExecEnd("done", RunStatuses.Succeeded, 0, null, false, null);
        store.ExecBegin("reported", T0);
        store.ExecEnd("reported", RunStatuses.Succeeded, 0, null, false, null);
        store.ExecReported("reported");

        Assert.Equal(["done"], store.UnreportedExecs().Select(r => r.RunId));
        Assert.Null(store.ExecRunOf("unknown"));
    }

    [Fact]
    public void Output_is_kept_in_order_a_repeated_chunk_changes_nothing_and_sent_output_is_deleted()
    {
        store.AddExecOutput(new StoredChunk("r1", 1, "stderr", "second", true));
        store.AddExecOutput(new StoredChunk("r1", 0, "stdout", "first", false));
        store.AddExecOutput(new StoredChunk("r1", 0, "stdout", "REPLACED", false));
        store.AddExecOutput(new StoredChunk("r2", 0, "stdout", "other", false));

        var unsent = store.UnsentExecOutput(10);
        Assert.Equal([("r1", 0, "first", false), ("r1", 1, "second", true), ("r2", 0, "other", false)], unsent.Select(c => (c.RunId, c.Seq, c.Body, c.Gap)));
        Assert.Equal(2, store.UnsentExecOutput(2).Count);
        Assert.True(store.HasUnsentOutput("r1"));

        store.ExecOutputSent("r1", 0);
        store.ExecOutputSent("r1", 1);
        Assert.False(store.HasUnsentOutput("r1"));
        Assert.True(store.HasUnsentOutput("r2"));
        Assert.Equal("r2", Assert.Single(store.UnsentExecOutput(10)).RunId); // deleted, not just marked
    }

    // ---- requests ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_request_added_twice_with_one_id_stays_as_first_written_and_new_ones_come_oldest_first()
    {
        store.AddRequest("b", "machines", "GET", "u2", null, T0.AddSeconds(2));
        store.AddRequest("a", "run", "POST", "u1", "{\"x\":1}", T0.AddSeconds(1));
        store.AddRequest("a", "other", "GET", "changed", null, T0.AddSeconds(9));

        var list = store.NewRequests(10);
        Assert.Equal(["a", "b"], list.Select(r => r.LocalId));
        Assert.Equal(("run", "POST", "u1", "{\"x\":1}"), (list[0].Kind, list[0].Method, list[0].Url, list[0].Body));
        Assert.Single(store.NewRequests(1));
    }

    [Fact]
    public void Forgetting_drops_answered_requests_older_than_the_limit_and_never_a_waiting_one()
    {
        foreach (var id in new[] { "old-done", "old-failed", "new-done", "waiting" }) store.AddRequest(id, "k", "GET", "u", null, T0);
        store.RequestAnswered("old-done", LocalStore.RequestStates.Done, "{}", null, T0.AddHours(-30));
        store.RequestAnswered("old-failed", LocalStore.RequestStates.Failed, null, "e", T0.AddHours(-30));
        store.RequestAnswered("new-done", LocalStore.RequestStates.Done, "{}", null, T0.AddHours(-1));

        store.ForgetRequests(T0.AddHours(-24));

        Assert.Null(store.Request("old-done"));
        Assert.Null(store.Request("old-failed"));
        Assert.NotNull(store.Request("new-done"));
        Assert.Equal(LocalStore.RequestStates.New, store.Request("waiting")!.State);
    }

    // ---- runs followed -----------------------------------------------------------------------------------------

    [Fact]
    public void A_followed_run_is_fetched_when_it_changed_or_was_last_fetched_before_the_stale_time_and_never_when_done()
    {
        store.FollowRun("fresh", RunStatuses.Approved, T0);
        store.RunFetched("fresh", RunStatuses.Approved, "{}", 0, false, T0); // fetched at T0, clean
        store.FollowRun("stale", RunStatuses.Approved, T0);
        store.RunFetched("stale", RunStatuses.Approved, "{}", 0, false, T0.AddHours(-1));
        store.FollowRun("dirty", RunStatuses.Approved, T0);
        store.RunFetched("dirty", RunStatuses.Approved, "{}", 0, false, T0);
        store.RunChanged("dirty");
        store.FollowRun("done", RunStatuses.Succeeded, T0);
        store.RunFetched("done", RunStatuses.Succeeded, "{}", 3, true, T0.AddHours(-5));
        store.RunChanged("done");

        var due = store.RunsToFetch(T0.AddMinutes(-5), 10).Select(r => r.RunId).Order().ToList();

        Assert.Equal(["dirty", "stale"], due);
        Assert.Equal("stale", Assert.Single(store.RunsToFetch(T0.AddMinutes(-5), 10), r => r.RunId == "stale").RunId);
        Assert.Single(store.RunsToFetch(T0.AddMinutes(-5), 1)); // the limit holds
    }

    [Fact]
    public void Following_a_run_again_marks_it_dirty_without_forgetting_how_far_its_output_was_read()
    {
        store.FollowRun("r1", RunStatuses.Approved, T0);
        Assert.Equal(-1, store.Followed("r1")!.NextSeq);
        store.RunFetched("r1", RunStatuses.Running, """{"status":"running"}""", 7, false, T0.AddMinutes(1));
        Assert.Empty(store.RunsToFetch(T0, 10));

        store.FollowRun("r1", RunStatuses.Approved, T0.AddMinutes(2));

        var run = Assert.Single(store.RunsToFetch(T0, 10));
        Assert.Equal((RunStatuses.Running, 7, """{"status":"running"}"""), (run.Status, run.NextSeq, run.View));
        Assert.Null(store.Followed("unknown"));
    }

    [Fact]
    public void Followed_output_is_kept_by_sequence_and_a_repeated_chunk_changes_nothing()
    {
        store.AddRunOutput("r1", [new StoredChunk("r1", 1, "stdout", "b", false), new StoredChunk("r1", 0, "stdout", "a", false)]);
        store.AddRunOutput("r1", [new StoredChunk("r1", 0, "stdout", "REPLACED", true), new StoredChunk("r1", 2, "stderr", "c", true)]);
        store.AddRunOutput("r2", [new StoredChunk("r2", 0, "stdout", "other", false)]);

        var output = store.RunOutput("r1");
        Assert.Equal([(0, "a", false), (1, "b", false), (2, "c", true)], output.Select(c => (c.Seq, c.Body, c.Gap)));
    }

    // ---- an older database -------------------------------------------------------------------------------------

    [Fact]
    public void A_database_made_before_remote_work_gains_its_tables_and_keeps_what_it_held()
    {
        using var old = new TempHome();
        using (var connection = new SqliteConnection($"Data Source={old.Config.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO kv VALUES ('settings.mask_secrets', 'false');
                CREATE TABLE outbox (id INTEGER PRIMARY KEY AUTOINCREMENT, harness TEXT NOT NULL,
                    session TEXT NOT NULL, kind TEXT NOT NULL, occurred_at TEXT NOT NULL, payload TEXT NOT NULL,
                    truncated INTEGER NOT NULL, project_key TEXT, project_name TEXT, git_branch TEXT, batch INTEGER);
                INSERT INTO outbox (harness, session, kind, occurred_at, payload, truncated) VALUES ('claude_code', 's1', 'note', '2026-10-01T00:00:00Z', '{}', 0);
                """;
            cmd.ExecuteNonQuery();
        }

        using (var upgraded = new LocalStore(old.Config.DatabasePath))
        {
            Assert.Equal("false", upgraded.Get("settings.mask_secrets"));
            Assert.Equal(1, upgraded.OutboxCount());
            Assert.True(upgraded.ExecBegin("r1", T0));
            upgraded.AddExecOutput(new StoredChunk("r1", 0, "stdout", "x", false));
            upgraded.AddRequest("q1", "run", "POST", "u", null, T0);
            upgraded.FollowRun("f1", RunStatuses.Approved, T0);
            upgraded.AddRunOutput("f1", [new StoredChunk("f1", 0, "stdout", "y", false)]);
            Assert.Equal(("q1", "f1", 1), (upgraded.NewRequests(5)[0].LocalId, upgraded.RunsToFetch(T0.AddDays(-1), 5)[0].RunId, upgraded.RunOutput("f1").Count));
        }

        using var again = new LocalStore(old.Config.DatabasePath); // a second open finds the tables and leaves them as they are
        Assert.False(again.ExecBegin("r1", T0));
        Assert.Equal("x", Assert.Single(again.UnsentExecOutput(5)).Body);
    }

    [Fact]
    public void Forgetting_runs_drops_old_finished_followed_runs_with_their_output_and_old_reported_executions_only()
    {
        var old = T0.AddDays(-8);
        var limit = T0.AddDays(-7);
        // followed: done+old (gone), done+recent (kept), not done+old (kept)
        foreach (var id in new[] { "f-old", "f-new", "f-open" })
        {
            store.FollowRun(id, RunStatuses.Succeeded, T0);
            store.AddRunOutput(id, [new StoredChunk(id, 0, "stdout", "out", false)]);
        }

        store.RunFetched("f-old", RunStatuses.Succeeded, "{}", 1, true, old);
        store.RunFetched("f-new", RunStatuses.Succeeded, "{}", 1, true, T0);
        store.RunFetched("f-open", RunStatuses.Running, "{}", 1, false, old);
        // executed: finished+reported+old (gone), finished+unreported+old, running+old, finished+reported+recent (kept)
        store.ExecBegin("e-old", old);
        store.ExecEnd("e-old", RunStatuses.Succeeded, 0, null, false, null);
        store.ExecReported("e-old");
        store.ExecBegin("e-unreported", old);
        store.ExecEnd("e-unreported", RunStatuses.Succeeded, 0, null, false, null);
        store.ExecBegin("e-running", old);
        store.ExecBegin("e-new", T0);
        store.ExecEnd("e-new", RunStatuses.Succeeded, 0, null, false, null);
        store.ExecReported("e-new");

        store.ForgetRuns(limit);

        Assert.Null(store.Followed("f-old"));
        Assert.Empty(store.RunOutput("f-old"));
        Assert.NotNull(store.Followed("f-new"));
        Assert.Single(store.RunOutput("f-new"));
        Assert.NotNull(store.Followed("f-open"));
        Assert.Single(store.RunOutput("f-open"));
        Assert.Null(store.ExecRunOf("e-old"));
        Assert.NotNull(store.ExecRunOf("e-unreported"));
        Assert.NotNull(store.ExecRunOf("e-running"));
        Assert.NotNull(store.ExecRunOf("e-new"));
    }
}
