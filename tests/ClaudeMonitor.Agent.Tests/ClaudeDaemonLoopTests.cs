using System.Net;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The daemon's Claude update loop: silent by default, and really wired in once both sides allow it. The binary it looks for is missing, so nothing real ever starts.</summary>
public sealed class ClaudeDaemonLoopTests
{
    private static async Task<(string Log, ClaudeKit Kit)> RunDaemon(bool machine, bool workspace, Func<ClaudeKit, FakeApi, bool> enough)
    {
        var kit = new ClaudeKit(c => c with
        {
            FlushEvery = TimeSpan.FromMilliseconds(20),
            SettingsEvery = TimeSpan.FromMilliseconds(20),
            UpdatePollEvery = TimeSpan.FromMilliseconds(50),
        }, installed: false);
        FakeApi fake = null!;
        fake = new FakeApi()
            .On("POST /api/agent/heartbeat", HttpStatusCode.NoContent, "")
            .On("GET /api/agent/settings", HttpStatusCode.OK, new AgentSettings(true, 1000, Guid.NewGuid(), ClaudeUpdate: workspace))
            .On("GET /api/agent/stream", _ =>
            {
                SpinWait.SpinUntil(() => enough(kit, fake), TimeSpan.FromSeconds(20)); // a condition, not a sleep
                return (HttpStatusCode.OK, "event: revoked\ndata: {}\n\n");
            });
        (Identity.Load(kit.Config) with { Server = "https://m.invalid", AgentId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid() }).Save(kit.Config);
        Credentials.For(kit.Config).Write(Credentials.Access, "a");
        SavedSettings.SaveClaudeUpdate(kit.Config, machine);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var host = new DaemonHost(kit.Config, TimeProvider.System, new AgentLog(kit.Config, TimeProvider.System), fake);
        Assert.Equal(0, await host.RunAsync(timeout.Token));
        Assert.False(timeout.IsCancellationRequested);
        Assert.True(fake.Count("GET /api/agent/settings") >= 1);
        return (kit.LogText, kit);
    }

    [Fact]
    public async Task With_both_sides_on_the_daemon_runs_the_loop_and_it_finds_no_claude_and_stops_there()
    {
        var (log, kit) = await RunDaemon(machine: true, workspace: true, (k, _) => k.State.Result == ClaudeCodes.NoClaude);
        using (kit)
        {
            Assert.Equal(ClaudeCodes.NoClaude, kit.State.Result);
            Assert.Contains("claude update no-claude", log, StringComparison.Ordinal);
            Assert.Contains("revoked from the web", log, StringComparison.Ordinal);
            Assert.False(File.Exists(kit.Config.ClaudeCancelPath));
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Without_both_sides_the_daemon_leaves_no_trace_of_the_loop_even_after_many_polls(bool machine, bool workspace)
    {
        // the daemon asks the server for settings every 20 ms and the loop polls every 50 ms: after 20 answers it has polled several times
        var (log, kit) = await RunDaemon(machine, workspace, (_, api) => api.Count("GET /api/agent/settings") >= 20);
        using (kit)
        {
            Assert.False(File.Exists(kit.Config.ClaudeUpdateStatePath));
            Assert.False(File.Exists(kit.Config.ClaudeUpdateLockPath));
            Assert.DoesNotContain("claude update", log, StringComparison.Ordinal);
            Assert.Contains("daemon started", log, StringComparison.Ordinal);
        }
    }
}
