using Podium.Agent.Daemon;

namespace Podium.Agent.Tests;

/// <summary>A standalone daemon that is not connected does not exit with 1 (ADR-0008 §3g): it waits for a login, so no supervisor restarts it in a loop.</summary>
public sealed class StandaloneDaemonTests : IDisposable
{
    private readonly TempHome home = new(c => c with { Standalone = true, StandaloneIdentityPoll = TimeSpan.FromMilliseconds(20) });

    public void Dispose() => home.Dispose();

    [Fact]
    public async Task It_stays_while_there_is_no_login_logs_it_once_and_ends_with_zero_when_it_is_stopped()
    {
        var log = new AgentLog(home.Config, TimeProvider.System);
        using var stop = new CancellationTokenSource();

        var run = DaemonRole.RunUntilAsync(home.Config, TimeProvider.System, log, stop.Token);
        await Task.Delay(300); // several polls
        Assert.False(run.IsCompleted, "it exited instead of waiting");
        await stop.CancelAsync();
        var code = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, code);
        var text = await File.ReadAllTextAsync(home.Config.LogPath);
        Assert.Equal(1, text.Split("standalone agent not connected").Length - 1);
        Assert.DoesNotContain("daemon started", text, StringComparison.Ordinal);
        Assert.False(File.Exists(home.Config.DatabasePath), "the daemon proper never started without a login");
    }

    [Fact]
    public void Standalone_mode_comes_from_the_environment_the_autostart_sets()
    {
        Agent.Config.AgentConfig With(string? value) => Agent.Config.AgentConfig.FromEnvironment(k => k == "AP_STANDALONE" ? value : null);
        Assert.True(With("1").Standalone);
        Assert.False(With(null).Standalone);
        Assert.False(With("0").Standalone);
        Assert.Equal(TimeSpan.FromSeconds(30), With(null).StandaloneIdentityPoll);
    }
}
