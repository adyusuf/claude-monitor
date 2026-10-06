using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;
using Microsoft.Data.Sqlite;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Several keys are written in one transaction: all of them or none, so a settings pass's values never sit under another pass's tag.</summary>
public sealed class LocalStoreSetManyTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly LocalStore store;
    private readonly LocalStore other;

    public LocalStoreSetManyTests()
    {
        store = new LocalStore(home.Config.DatabasePath);
        other = new LocalStore(home.Config.DatabasePath); // a second connection, as the stream's relay has
    }

    public void Dispose()
    {
        other.Dispose();
        store.Dispose();
        home.Dispose();
    }

    [Fact]
    public void Every_key_is_written_and_an_existing_one_is_replaced_and_another_connection_reads_them()
    {
        store.Set("b", "old");
        store.SetMany([("a", "1"), ("b", "2"), ("c", "3")]);
        Assert.Equal(("1", "2", "3"), (other.Get("a"), other.Get("b"), other.Get("c")));
        store.SetMany([]);
        Assert.Equal("2", other.Get("b"));
    }

    [Fact]
    public void A_failure_part_way_leaves_every_key_as_it_was()
    {
        store.Set("a", "before");
        store.Set("untouched", "x");

        // a null value breaks the NOT NULL constraint on the third entry, after the first two were already written
        Assert.Throws<SqliteException>(() => store.SetMany([("a", "after"), ("new", "1"), ("broken", null!)]));

        Assert.Equal(("before", null, null), (other.Get("a"), other.Get("new"), other.Get("broken")));
        Assert.Equal("x", other.Get("untouched"));
        store.SetMany([("a", "later")]); // the connection is usable again: no transaction was left open
        Assert.Equal("later", other.Get("a"));
    }

    [Fact]
    public void The_remembered_settings_are_the_switch_and_the_thresholds_only_when_there_are_some()
    {
        Assert.Equal([(MachineMonitor.RemoteRunsKey, "false")], MachineMonitor.Entries(new AgentSettings(true, 1000, Guid.NewGuid())));
        Assert.Equal([(MachineMonitor.RemoteRunsKey, "true"), (MachineMonitor.ThresholdsKey, "80,81,82,83")],
            MachineMonitor.Entries(new AgentSettings(true, 1000, Guid.NewGuid(), RemoteRuns: true, Alerts: new AlertThresholds(80, 81, 82, 83))));
    }
}
