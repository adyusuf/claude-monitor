using System.Net;
using System.Text.Json;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;
using Microsoft.Data.Sqlite;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The moment a hook handed a command to its session is kept, and is what the web is told (not the report's time).</summary>
public sealed class CommandTimeTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow);

    public void Dispose() => home.Dispose();

    [Fact]
    public async Task The_report_carries_the_time_the_hook_took_the_command_not_the_time_of_the_report()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        var fake = new FakeApi().On("POST /api/agent/commands/*", HttpStatusCode.NoContent, "");
        using var http = ApiClient.CreateHttp("https://monitor.invalid", fake);
        var creds = Credentials.For(home.Config);
        creds.Write(Credentials.Access, "access-1");
        creds.Write(Credentials.Refresh, "refresh-1");
        using var api = new ApiClient(http, creds, home.Config.ApiCallTimeout);
        var relay = new Relay(home.Config, store, api, clock);

        store.SaveCommand(new LocalCommand("c1", "s1", CommandKinds.Prompt, "hi", clock.GetUtcNow().AddMinutes(5)));
        var tookIt = clock.GetUtcNow();
        Assert.NotNull(store.TakeCommand("s1", CommandKinds.Prompt, tookIt));
        clock.Advance(TimeSpan.FromSeconds(2)); // the daemon reports on its next round
        await relay.ReportCommandsAsync(CancellationToken.None);

        var sent = JsonSerializer.Deserialize<CommandStatusUpdate>(fake.Seen.Single(s => s.Path.EndsWith("/status", StringComparison.Ordinal)).Body, ApiClient.Json)!;
        Assert.Equal(CommandStatuses.Applied, sent.Status);
        Assert.Equal(tookIt, sent.At);
    }

    [Fact]
    public void A_database_an_older_agent_made_gains_the_column_and_its_taken_commands_report_no_time()
    {
        Directory.CreateDirectory(home.Dir);
        using (var old = new SqliteConnection($"Data Source={home.Config.DatabasePath};Pooling=False"))
        {
            old.Open();
            using var create = old.CreateCommand();
            create.CommandText = """
                CREATE TABLE commands (id TEXT PRIMARY KEY, session TEXT NOT NULL, kind TEXT NOT NULL,
                    body TEXT, expires_at TEXT NOT NULL, state TEXT NOT NULL);
                INSERT INTO commands VALUES ('old', 's1', 'prompt', 'b', '2099-01-01T00:00:00.0000000+00:00', 'taken');
                """;
            create.ExecuteNonQuery();
        }

        using var store = new LocalStore(home.Config.DatabasePath);
        Assert.Equal([new TakenCommand("old", null)], store.TakenCommands());
        using var again = new LocalStore(home.Config.DatabasePath); // opening twice must not add the column twice
        var at = clock.GetUtcNow();
        store.SaveCommand(new LocalCommand("new", "s1", CommandKinds.Prompt, "b", at.AddMinutes(5)));
        store.TakeCommand("s1", CommandKinds.Prompt, at);
        Assert.Contains(new TakenCommand("new", at), store.TakenCommands());
    }
}
