using Microsoft.Data.Sqlite;
using Podium.Agent.Storage;

namespace Podium.Agent.Tests;

/// <summary>F-323: the run's process in the agent's own database, added to a database that is already there.</summary>
public sealed class LocalStoreExecPidTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private readonly TempHome home = new();

    public void Dispose() => home.Dispose();

    [Fact]
    public void A_started_runs_process_is_kept_and_read_back_and_a_run_without_one_reads_null()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        store.ExecBegin("r1", T0);
        store.ExecBegin("r2", T0);

        store.ExecStarted("r1", 777, T0.AddSeconds(1), T0.AddMinutes(5));

        var kept = store.ExecRunOf("r1")!;
        Assert.Equal((777, T0.AddSeconds(1), T0.AddMinutes(5)), (kept.Pid, kept.PidStart, kept.Deadline));
        var none = store.ExecRunOf("r2")!;
        Assert.Equal((null, null, null), (none.Pid, none.PidStart, none.Deadline));
    }

    [Fact]
    public void A_finished_run_is_not_given_a_process_after_the_fact()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        store.ExecBegin("r1", T0);
        store.ExecEnd("r1", "failed", null, "spawn_failed", false, null);

        store.ExecStarted("r1", 777, T0, T0.AddMinutes(5));

        Assert.Null(store.ExecRunOf("r1")!.Pid);
    }

    [Fact]
    public void A_database_made_before_the_process_was_kept_gets_the_columns_and_its_running_row_reads_null()
    {
        using (var old = new SqliteConnection($"Data Source={home.Config.DatabasePath}"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE exec_runs (run_id TEXT PRIMARY KEY, state TEXT NOT NULL, final_status TEXT, exit_code INTEGER,
                    error TEXT, truncated INTEGER NOT NULL DEFAULT 0, resolved_exe TEXT, started_at TEXT NOT NULL, reported INTEGER NOT NULL DEFAULT 0);
                INSERT INTO exec_runs (run_id, state, started_at) VALUES ('old', 'running', '2026-10-06T12:00:00.0000000+00:00');
                """;
            cmd.ExecuteNonQuery();
        }

        using var store = new LocalStore(home.Config.DatabasePath);

        var row = store.ExecRunOf("old")!;
        Assert.Equal(("running", null, null, null), (row.State, row.Pid, row.PidStart, row.Deadline));
        store.ExecStarted("old", 5, T0, T0.AddMinutes(1));
        Assert.Equal(5, store.ExecRunOf("old")!.Pid);
        SqliteConnection.ClearAllPools();
    }
}
