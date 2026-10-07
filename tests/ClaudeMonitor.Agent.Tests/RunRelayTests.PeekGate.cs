using System.Net;
using System.Text.Json;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>A settings pass takes the workspace it tags with and the time it begins at as one step.</summary>
public sealed partial class RunRelayTests
{
    // The first reading of the clock blocks until released (the pass is "pre-empted" right after it peeked the workspace); every
    // later reading moves the manual clock on one second first, so a pass that begins after another always has a later stamp.
    private sealed class PreemptedClock(ManualClock inner, ManualResetEventSlim reading, ManualResetEventSlim release) : TimeProvider
    {
        private readonly Lock gate = new();
        private int first;

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Exchange(ref first, 1) == 0)
            {
                reading.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                lock (gate) return inner.GetUtcNow();
            }

            lock (gate)
            {
                inner.Advance(TimeSpan.FromSeconds(1));
                return inner.GetUtcNow();
            }
        }
    }

    [Fact]
    public async Task A_pass_pre_empted_between_peeking_the_workspace_and_reading_the_clock_cannot_be_overtaken_by_a_later_pass()
    {
        if (!ExecFixture.Unix) return;
        using var reading = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var laterRequested = new ManualResetEventSlim();
        TestWorkspace.Id(fx.Home.Config); // W1: what pass X peeks
        fx.Fake.On("GET /api/agent/settings", _ =>
        {
            laterRequested.Set();
            return (HttpStatusCode.OK, JsonSerializer.Serialize(new AgentSettings(true, 1000, Identity.Peek(fx.Home.Config)?.WorkspaceId ?? Guid.Empty, RemoteRuns: true), ApiClient.Json));
        });
        var timed = new Relay(fx.Home.Config, fx.Store, fx.Api, new PreemptedClock(fx.Clock, reading, release)) { Runs = relay };

        var x = Task.Run(() => timed.SettingsAsync(CancellationToken.None));
        Assert.True(reading.Wait(TimeSpan.FromSeconds(10))); // X has peeked W1 and is held at its clock reading
        var moved = Identity.Peek(fx.Home.Config)! with { WorkspaceId = Guid.NewGuid() };
        moved.Save(fx.Home.Config); // `cm-agent login` moved the machine to W2
        var later = Task.Run(() => timed.SettingsAsync(CancellationToken.None));

        // A bounded wait for something that must NOT happen: while X holds the gate the later pass cannot begin, so its request never goes out.
        Assert.False(laterRequested.Wait(TimeSpan.FromMilliseconds(500)));
        release.Set();
        await Task.WhenAll(x, later).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(moved.WorkspaceId!.Value.ToString("D"), fx.Store.Get(WorkspaceSettings.WorkspaceKey)); // the later pass's tag survived
        Assert.False(WorkspaceSettings.TagIsStale(fx.Home.Config, fx.Store));
        Assert.Equal("true", WorkspaceSettings.Get(fx.Home.Config, fx.Store, MachineMonitor.RemoteRunsKey));
    }
}
