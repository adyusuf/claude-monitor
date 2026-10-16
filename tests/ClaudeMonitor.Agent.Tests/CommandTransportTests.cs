using System.Net;
using System.Text.Json;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;
using Microsoft.Data.Sqlite;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>How a command reached its session (ADR-0007 section 9): kept locally with the command, reported with "applied", never invented.</summary>
public sealed class CommandTransportTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => home.Dispose();

    private LocalCommand Queue(LocalStore store, string id)
    {
        var c = new LocalCommand(id, "s1", CommandKinds.Prompt, "hi", clock.GetUtcNow().AddMinutes(30));
        store.SaveCommand(c);
        return c;
    }

    [Fact]
    public void A_command_a_hook_took_is_a_hook_command_and_a_queued_one_has_no_transport_yet()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        Queue(store, "c1");
        Assert.Empty(store.TakenCommands());
        Assert.NotNull(store.TakeCommand("s1", CommandKinds.Prompt, clock.GetUtcNow()));
        Assert.Equal(CommandTransports.Hook, Assert.Single(store.TakenCommands()).Transport);
    }

    [Theory]
    [InlineData(CommandTransports.Inbox)]
    [InlineData(CommandTransports.Channel)]
    public void A_pushed_command_keeps_the_way_it_was_pushed_through_its_confirmation(string transport)
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        Queue(store, "c1");
        Assert.Single(store.TakePromptsForPush("s1", CommandKinds.Prompt, clock.GetUtcNow(), transport));
        Assert.True(store.ConfirmPush("c1", clock.GetUtcNow()));
        Assert.Equal(transport, Assert.Single(store.TakenCommands()).Transport);
    }

    [Fact]
    public void A_push_that_failed_leaves_no_transport_and_the_hook_that_then_takes_it_is_the_transport()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        Queue(store, "c1");
        store.TakePromptsForPush("s1", CommandKinds.Prompt, clock.GetUtcNow(), CommandTransports.Inbox);
        store.PushFailed("c1");
        Assert.Equal("c1", store.TakeCommand("s1", CommandKinds.Prompt, clock.GetUtcNow())!.Id);
        Assert.Equal(CommandTransports.Hook, Assert.Single(store.TakenCommands()).Transport);
    }

    [Fact]
    public async Task The_report_carries_the_transport()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        var fake = new FakeApi().On("POST /api/agent/commands/*", HttpStatusCode.NoContent, "");
        using var http = ApiClient.CreateHttp("https://monitor.invalid", fake);
        var creds = Credentials.For(home.Config);
        creds.Write(Credentials.Access, "access-1");
        creds.Write(Credentials.Refresh, "refresh-1");
        using var api = new ApiClient(http, creds, home.Config.ApiCallTimeout);
        Queue(store, "c1");
        store.TakePromptsForPush("s1", CommandKinds.Prompt, clock.GetUtcNow(), CommandTransports.Inbox);
        store.ConfirmPush("c1", clock.GetUtcNow());

        await new Relay(home.Config, store, api, clock).ReportCommandsAsync(CancellationToken.None);

        var sent = JsonSerializer.Deserialize<CommandStatusUpdate>(fake.Seen.Single(s => s.Path.EndsWith("/status", StringComparison.Ordinal)).Body, ApiClient.Json)!;
        Assert.Equal((CommandStatuses.Applied, CommandTransports.Inbox), (sent.Status, sent.Transport));
    }

    [Fact]
    public void A_database_an_older_agent_made_gains_the_column_and_a_command_it_took_reports_no_transport()
    {
        Directory.CreateDirectory(home.Dir);
        using (var old = new SqliteConnection($"Data Source={home.Config.DatabasePath};Pooling=False"))
        {
            old.Open();
            using var create = old.CreateCommand();
            create.CommandText = """
                CREATE TABLE commands (id TEXT PRIMARY KEY, session TEXT NOT NULL, kind TEXT NOT NULL,
                    body TEXT, expires_at TEXT NOT NULL, state TEXT NOT NULL, taken_at TEXT, push_tried INTEGER);
                INSERT INTO commands VALUES ('old', 's1', 'prompt', 'b', '2099-01-01T00:00:00.0000000+00:00', 'taken', NULL, NULL);
                """;
            create.ExecuteNonQuery();
        }

        using var store = new LocalStore(home.Config.DatabasePath);
        using var again = new LocalStore(home.Config.DatabasePath); // opening twice must not add the column twice
        Assert.Equal([new TakenCommand("old", null, null)], store.TakenCommands());
    }

    // ---- the push the web is told about before the command is applied --------------------------------------------------

    private (Relay Relay, FakeApi Fake, ApiClient Api, HttpClient Http) NewRelay(LocalStore store)
    {
        var fake = new FakeApi().On("POST /api/agent/commands/*", HttpStatusCode.NoContent, "");
        var http = ApiClient.CreateHttp("https://monitor.invalid", fake);
        var creds = Credentials.For(home.Config);
        creds.Write(Credentials.Access, "access-1");
        creds.Write(Credentials.Refresh, "refresh-1");
        var api = new ApiClient(http, creds, home.Config.ApiCallTimeout);
        return (new Relay(home.Config, store, api, clock), fake, api, http);
    }

    private static List<CommandStatusUpdate> Reports(FakeApi fake) =>
        [.. fake.Seen.Where(s => s.Path.EndsWith("/status", StringComparison.Ordinal)).Select(s => JsonSerializer.Deserialize<CommandStatusUpdate>(s.Body, ApiClient.Json)!)];

    [Fact]
    public async Task A_pushed_command_is_reported_once_as_delivered_with_its_transport_and_then_as_applied_when_the_session_shows_it()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        var (relay, fake, api, http) = NewRelay(store);
        using (api) using (http)
        {
            Queue(store, "c1");
            store.TakePromptsForPush("s1", CommandKinds.Prompt, clock.GetUtcNow(), CommandTransports.Inbox);

            await relay.ReportCommandsAsync(CancellationToken.None);
            await relay.ReportCommandsAsync(CancellationToken.None); // nothing new
            var pushed = Assert.Single(Reports(fake));
            Assert.Equal((CommandStatuses.Delivered, CommandTransports.Inbox, clock.GetUtcNow()), (pushed.Status, pushed.Transport, pushed.At));

            store.ConfirmPush("c1", clock.GetUtcNow());
            await relay.ReportCommandsAsync(CancellationToken.None);
            var all = Reports(fake);
            Assert.Equal([CommandStatuses.Delivered, CommandStatuses.Applied], all.Select(r => r.Status));
            Assert.Equal(CommandTransports.Inbox, all[1].Transport);
        }
    }

    [Fact]
    public async Task A_push_given_up_after_the_web_was_told_is_taken_back_once_and_one_never_told_is_not()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        var (relay, fake, api, http) = NewRelay(store);
        using (api) using (http)
        {
            Queue(store, "told");
            Queue(store, "quiet");
            store.TakePromptsForPush("s1", CommandKinds.Prompt, clock.GetUtcNow(), CommandTransports.Channel);
            store.PushFailed("quiet"); // given up before the daemon's next pass: the web never heard of it
            await relay.ReportCommandsAsync(CancellationToken.None);
            Assert.Equal("channel", Assert.Single(Reports(fake)).Transport);

            store.PushFailed("told");
            await relay.ReportCommandsAsync(CancellationToken.None);
            await relay.ReportCommandsAsync(CancellationToken.None);
            var taken = Reports(fake).Skip(1).ToList();
            Assert.Equal((CommandStatuses.Delivered, ""), (Assert.Single(taken).Status, taken[0].Transport));
            Assert.Empty(store.PushRetractions());
        }
    }

    [Fact]
    public async Task A_command_a_hook_takes_is_never_reported_as_pushed()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        var (relay, fake, api, http) = NewRelay(store);
        using (api) using (http)
        {
            Queue(store, "c1");
            store.TakeCommand("s1", CommandKinds.Prompt, clock.GetUtcNow());
            await relay.ReportCommandsAsync(CancellationToken.None);
            var only = Assert.Single(Reports(fake));
            Assert.Equal((CommandStatuses.Applied, CommandTransports.Hook), (only.Status, only.Transport));
        }
    }

    [Fact]
    public void A_database_an_older_agent_made_gains_the_push_columns_and_tables()
    {
        Directory.CreateDirectory(home.Dir);
        using (var old = new SqliteConnection($"Data Source={home.Config.DatabasePath};Pooling=False"))
        {
            old.Open();
            using var create = old.CreateCommand();
            create.CommandText = "CREATE TABLE pushes (command_id TEXT PRIMARY KEY, pushed_at TEXT NOT NULL, confirmed_at TEXT);";
            create.ExecuteNonQuery();
        }

        using var store = new LocalStore(home.Config.DatabasePath);
        using var again = new LocalStore(home.Config.DatabasePath);
        Assert.Empty(store.PushesToReport());
        Assert.Empty(store.PushRetractions());
    }
}
