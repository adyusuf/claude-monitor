using Aipod.Tray;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Push;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;

namespace Aipod.Tray.Tests;

public sealed class StatusTests : IDisposable
{
    private readonly AgentHome home = new();
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => home.Dispose();

    private StatusReader Reader() => new(home.Config, clock);

    private LocalStore Store() => new(home.Config.DatabasePath);

    [Fact]
    public void A_machine_that_never_connected_says_so_without_creating_anything()
    {
        var status = Reader().Read();

        Assert.Equal(("not connected", false, false), (status.Headline, status.Connected, status.DaemonRunning));
        Assert.Equal((0L, 0L), (status.Incoming, status.Outgoing));
        Assert.Contains("not connected", status.Tooltip(), StringComparison.Ordinal);
        Assert.False(File.Exists(home.Config.DatabasePath));
        Assert.Equal((0L, 0L), Reader().ReadCounters());
    }

    [Fact]
    public void A_connected_machine_whose_agent_has_not_started_says_it_is_waiting()
    {
        home.Connect();
        Assert.Equal("waiting for the agent to start", Reader().Read().Headline);
    }

    [Fact]
    public void With_a_database_but_no_daemon_the_state_is_connected_but_not_running_and_the_counters_and_version_come_from_it()
    {
        home.Connect();
        using (var store = Store())
        {
            store.Bump(PushStatus.InCountKey);
            store.Bump(PushStatus.InCountKey);
            store.Bump(PushStatus.OutCountKey);
            store.Set(UpdateHealth.VersionKey, "0.9.1");
        }

        var status = Reader().Read();

        Assert.Equal("connected, the agent is not running", status.Headline);
        Assert.Equal(("0.9.1", 2L, 1L), (status.Version, status.Incoming, status.Outgoing));
        Assert.Equal((2L, 1L), Reader().ReadCounters());
        Assert.NotEmpty(status.Lines);
    }

    [Theory]
    [InlineData(PushStatus.Connected, "connected")]
    [InlineData(PushStatus.Reconnecting, "reconnecting to the server")]
    [InlineData("", "connected, stream not open yet")]
    public void With_the_daemon_running_the_headline_follows_the_stream(string stream, string headline)
    {
        home.Connect();
        using (var store = Store()) store.Set(PushStatus.StreamStateKey, stream);
        using var daemon = DaemonHost.TryLock(home.Config.LockPath); // what a running daemon holds

        var status = Reader().Read();

        Assert.True(status.DaemonRunning);
        Assert.Equal(headline, status.Headline);
    }

    [Fact]
    public void A_refused_agent_is_shown_as_disconnected_with_the_reason_in_the_lines()
    {
        using (var store = Store()) store.Set(AuthRefusal.ReasonKey, "token_expired");

        var status = Reader().Read();

        Assert.StartsWith("disconnected: the API refused this agent", status.Headline, StringComparison.Ordinal);
        Assert.Contains(status.Lines, l => l.Contains("credentials expired", StringComparison.Ordinal));
    }

    [Fact]
    public void An_update_waiting_shows_in_the_status_the_tooltip_and_the_menu()
    {
        home.Connect();
        UpdateState.Change(home.Config, s => s with { Available = "0.9.0" });

        var status = Reader().Read();

        Assert.Equal("0.9.0", status.UpdateAvailable);
        Assert.Contains("update 0.9.0 available", status.Tooltip(), StringComparison.Ordinal);
        Assert.Contains(TrayMenu.Build(status), e => e.Label == "Update 0.9.0 is available" && !e.Enabled);
    }

    [Fact]
    public void A_database_that_cannot_be_read_becomes_a_sentence_not_an_exception()
    {
        File.WriteAllText(home.Config.DatabasePath, "this is not a database");

        var status = Reader().Read();

        Assert.StartsWith("cannot read the agent's status (", status.Headline, StringComparison.Ordinal);
        Assert.Equal((0L, 0L), Reader().ReadCounters());
    }

    [Fact]
    public void The_tooltip_is_cut_to_fit_the_windows_limit()
    {
        var status = new TrayStatus("0.5.0", true, true, null, new string('x', 400), [], "1.0.0", 0, 0);
        Assert.Equal(120, status.Tooltip().Length);
        Assert.EndsWith("…", status.Tooltip(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-5, "0 s")]
    [InlineData(3, "3 s")]
    [InlineData(119, "119 s")]
    [InlineData(300, "5 min")]
    public void An_age_is_in_seconds_then_minutes(int seconds, string text) =>
        Assert.Equal(text, StatusReader.Age(TimeSpan.FromSeconds(seconds)));
}

public sealed class MenuTests
{
    private static TrayStatus Status(bool connected, string? server = "https://aipod.example.test", string? update = null) =>
        new("0.5.0", connected, connected, server, connected ? "connected" : "not connected", [], update, 0, 0);

    [Fact]
    public void A_connected_machine_gets_every_command_and_the_version_and_state_on_top()
    {
        var menu = TrayMenu.Build(Status(true));

        Assert.Equal("aipod 0.5.0", menu[0].Label);
        Assert.Equal("Connected (aipod.example.test)", menu[1].Label);
        Assert.All(menu.Take(2), e => Assert.False(e.Enabled));
        Assert.Equal(TrayCommand.Quit, menu[^1].Command);
        Assert.True(menu.Where(e => e.Command is not null).All(e => e.Enabled || e.Command == TrayCommand.UpdateNow));
        Assert.Contains(menu, e => e.Command == TrayCommand.Login && e.Label == "Log in again…");
        Assert.Contains(menu, e => e.Command == TrayCommand.UpdateNow && !e.Enabled && e.Label == "Update now");
        Assert.Equal(Enum.GetValues<TrayCommand>().Length - 0, menu.Where(e => e.Command is not null).Select(e => e.Command).Distinct().Count()); // each command once
    }

    [Fact]
    public void A_machine_that_is_not_connected_can_only_log_in_look_at_the_log_and_quit()
    {
        var menu = TrayMenu.Build(Status(false, server: null));

        var enabled = menu.Where(e => e.Command is not null && e.Enabled).Select(e => e.Command).ToList();
        Assert.Equal([TrayCommand.ShowWindow, TrayCommand.Login, TrayCommand.OpenLog, TrayCommand.Quit], enabled);
        Assert.Equal("Not connected", menu[1].Label);
        Assert.Contains(menu, e => e.Command == TrayCommand.Login && e.Label == "Log in…");
    }

    [Fact]
    public void An_update_waiting_adds_a_line_and_enables_the_update_command_with_its_version()
    {
        var menu = TrayMenu.Build(Status(true, update: "0.6.0"));

        Assert.Contains(menu, e => e.Label == "Update 0.6.0 is available" && e.Command is null && !e.Enabled);
        Assert.Contains(menu, e => e.Command == TrayCommand.UpdateNow && e.Enabled && e.Label == "Update to 0.6.0");
    }

    [Fact]
    public void Separators_are_empty_entries_without_a_command()
    {
        var menu = TrayMenu.Build(Status(true));
        Assert.Contains(menu, e => e.IsSeparator);
        Assert.DoesNotContain(menu, e => e.IsSeparator && e.Command is not null);
        Assert.Throws<ArgumentNullException>(() => TrayMenu.Build(null!));
    }
}
