using System.Net;
using System.Text.Json;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The time stamp of the settings read: a clock stepped back, and two passes that overlap.</summary>
public sealed partial class RunRelayTests
{
    // Answers the settings route from a sequence: the first read says off, the later ones say what `later` says.
    private void SettingsSequence(bool later)
    {
        var reads = 0;
        var workspace = TestWorkspace.Id(fx.Home.Config); // in agent.json before a pass begins, which tags its values with it
        fx.Fake.On("GET /api/agent/settings", _ =>
        {
            var n = Interlocked.Increment(ref reads);
            return (HttpStatusCode.OK, JsonSerializer.Serialize(new AgentSettings(true, 1000, workspace, RemoteRuns: n > 1 && later), ApiClient.Json));
        });
    }

    [Fact]
    public async Task A_clock_stepped_back_after_a_settings_read_does_not_hide_a_switch_turned_on_since()
    {
        if (!ExecFixture.Unix) return;
        SettingsSequence(later: true);
        var timed = new Relay(fx.Home.Config, fx.Store, fx.Api, fx.Clock) { Runs = relay };
        await timed.SettingsAsync(CancellationToken.None); // reads off, stamped now
        fx.Clock.Advance(TimeSpan.FromHours(-1)); // NTP or a person steps the clock back: the stamp is now in the future

        var run = Shell("echo after-step-back", fx.Clock.GetUtcNow());
        Routes(run.Id);
        Assert.True(await timed.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None));

        Assert.True(await Finished(run.Id));
        Assert.Equal(RunStatuses.Succeeded, fx.Store.ExecRunOf(run.Id.ToString())!.FinalStatus);
        Assert.Equal(2, fx.Fake.Count("GET /api/agent/settings"));
    }

    [Fact]
    public async Task A_read_after_a_clock_step_back_replaces_the_stamp_so_a_burst_of_runs_costs_one_read()
    {
        if (!ExecFixture.Unix) return;
        SettingsSequence(later: false);
        var timed = new Relay(fx.Home.Config, fx.Store, fx.Api, fx.Clock) { Runs = relay };
        await timed.SettingsAsync(CancellationToken.None);
        fx.Clock.Advance(TimeSpan.FromHours(-1));

        var decided = fx.Clock.GetUtcNow().AddMinutes(-1);
        foreach (var run in new[] { Shell("echo 1", decided), Shell("echo 2", decided), Shell("echo 3", decided) })
        {
            Assert.True(await timed.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None));
            Assert.Equal(RemoteErrors.Disabled, fx.Store.ExecRunOf(run.Id.ToString())!.Error);
        }

        Assert.Equal(2, fx.Fake.Count("GET /api/agent/settings")); // the one before the step back and one after it
    }

    [Fact]
    public async Task A_slower_pass_that_began_earlier_does_not_overwrite_what_a_later_pass_stored()
    {
        if (!ExecFixture.Unix) return;
        using var firstArrived = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reads = 0;
        var workspace = TestWorkspace.Id(fx.Home.Config);
        // Pass 1 reaches the server first (switch off) but its answer is held back; pass 2 begins later and completes first (on).
        fx.Fake.On("GET /api/agent/settings", _ =>
        {
            var n = Interlocked.Increment(ref reads);
            if (n == 1)
            {
                firstArrived.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }

            return (HttpStatusCode.OK, JsonSerializer.Serialize(new AgentSettings(true, 1000, workspace, RemoteRuns: n > 1), ApiClient.Json));
        });
        var timed = new Relay(fx.Home.Config, fx.Store, fx.Api, fx.Clock) { Runs = relay };

        var first = Task.Run(() => timed.SettingsAsync(CancellationToken.None));
        Assert.True(firstArrived.Wait(TimeSpan.FromSeconds(10)));
        fx.Clock.Advance(TimeSpan.FromSeconds(1));
        await timed.SettingsAsync(CancellationToken.None);
        Assert.Equal("true", WorkspaceSettings.Get(fx.Home.Config, fx.Store, MachineMonitor.RemoteRunsKey));

        release.Set();
        await first;

        Assert.Equal("true", WorkspaceSettings.Get(fx.Home.Config, fx.Store, MachineMonitor.RemoteRunsKey)); // the older answer was dropped
        var run = Shell("echo between-passes", fx.Clock.GetUtcNow().AddSeconds(-1)); // decided between the two passes' starts
        Routes(run.Id);
        Assert.True(await timed.OnStreamAsync(AgentStreamEvents.Run, Message(run), CancellationToken.None));
        Assert.True(await Finished(run.Id));
        Assert.Equal(2, fx.Fake.Count("GET /api/agent/settings"));
    }
}
