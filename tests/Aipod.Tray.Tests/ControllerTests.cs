using Aipod.Tray;
using ClaudeMonitor.Agent.Push;
using ClaudeMonitor.Agent.Storage;

namespace Aipod.Tray.Tests;

public sealed class ControllerTests : IDisposable
{
    private readonly AgentHome home = new();
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeCli cli = new();
    private readonly TrayController controller;

    public ControllerTests() =>
        controller = new TrayController(new StatusReader(home.Config, clock), new TrayActions(home.Config, cli), new LedBlinker(clock));

    public void Dispose() => home.Dispose();

    [Fact]
    public void Before_the_first_reading_the_status_is_a_placeholder_and_the_lights_are_off()
    {
        Assert.Equal("starting", controller.Status.Headline);
        Assert.Equal(Leds.Off, controller.Leds);
        Assert.False(controller.Busy);
        Assert.Equal("", controller.LastResult);
    }

    [Fact]
    public void A_refresh_reads_the_status_and_tells_the_listeners()
    {
        var changed = 0;
        controller.Changed += () => changed++;

        controller.Refresh();

        Assert.Equal(("not connected", 1), (controller.Status.Headline, changed));
    }

    [Fact]
    public void A_message_in_or_out_lights_the_matching_light_after_the_counters_are_read()
    {
        using (var store = new LocalStore(home.Config.DatabasePath))
        {
            store.Bump(PushStatus.InCountKey);
            store.Bump(PushStatus.OutCountKey);
        }

        controller.RefreshLights(); // the first reading: a starting point, nothing lights
        Assert.Equal(Leds.Off, controller.Leds);

        using (var store = new LocalStore(home.Config.DatabasePath)) store.Bump(PushStatus.InCountKey);
        controller.RefreshLights();
        Assert.Equal(new Leds(true, false), controller.Leds);

        using (var store = new LocalStore(home.Config.DatabasePath)) store.Bump(PushStatus.OutCountKey);
        controller.Refresh(); // the full read feeds the lights too
        Assert.True(controller.Leds.Green);
    }

    [Fact]
    public async Task A_command_runs_once_at_a_time_shows_its_result_and_refreshes_afterwards()
    {
        var release = new TaskCompletionSource<CliResult>();
        cli.Answer = _ => release.Task;
        var changes = new List<string>();
        controller.Changed += () => changes.Add(controller.LastResult);

        var first = controller.RunAsync(TrayCommand.Reconnect, null, CancellationToken.None);
        Assert.True(controller.Busy);
        await controller.RunAsync(TrayCommand.Disconnect, null, CancellationToken.None); // ignored while busy
        Assert.Single(cli.Runs);

        release.SetResult(new CliResult(0, "done"));
        await first;

        Assert.False(controller.Busy);
        Assert.Equal("done", controller.LastResult);
        Assert.Equal(["…", "done"], changes);
    }

    [Fact]
    public async Task A_command_that_throws_leaves_the_controller_free_again()
    {
        cli.Answer = _ => throw new InvalidOperationException();
        await controller.RunAsync(TrayCommand.Reconnect, null, CancellationToken.None);
        Assert.False(controller.Busy);
        Assert.Contains("InvalidOperationException", controller.LastResult, StringComparison.Ordinal);
    }
}
