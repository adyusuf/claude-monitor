using System.Diagnostics;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Update;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>
/// The real DaemonControl.Kill: it ends only the process the daemon wrote into daemon.pid, only while the daemon's lock is held
/// and only when that process is still a cm-agent. Never anything else (a recycled process id must not take an unrelated program).
/// </summary>
public sealed class DaemonControlKillTests : IDisposable
{
    private readonly TempHome home = new(c => c with { UpdateStopWait = TimeSpan.FromSeconds(10) });

    public void Dispose() => home.Dispose();

    private DaemonControl Control() => new(home.Config, new AgentLog(home.Config, TimeProvider.System), TimeProvider.System);

    private string Log => File.Exists(home.Config.LogPath) ? File.ReadAllText(home.Config.LogPath) : "";

    private static void AssertThisProcessLives()
    {
        using var me = Process.GetProcessById(Environment.ProcessId);
        Assert.False(me.HasExited);
        Assert.False(me.ProcessName.StartsWith("cm-agent", StringComparison.Ordinal), "the premise: the test process is not a cm-agent");
    }

    [Fact]
    public void With_no_daemon_the_lock_is_free_and_nothing_is_touched()
    {
        File.WriteAllText(home.Config.PidPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(Control().Kill());

        AssertThisProcessLives();
        Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(home.Config.PidPath));
        Assert.DoesNotContain("killing", Log, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_daemon_and_no_pid_file_it_is_true_too()
    {
        Assert.True(Control().Kill());
        Assert.False(File.Exists(home.Config.PidPath));
    }

    [Fact]
    public void A_pid_that_names_a_process_that_is_not_a_cm_agent_is_never_killed()
    {
        using var held = DaemonHost.TryLock(home.Config.LockPath); // "a daemon runs"
        Assert.NotNull(held);
        File.WriteAllText(home.Config.PidPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)); // the test host: dotnet or testhost

        Assert.False(Control().Kill());

        AssertThisProcessLives();
        Assert.DoesNotContain("killing", Log, StringComparison.Ordinal);
        Assert.True(Control().IsRunning()); // the lock is still held: nothing was released
    }

    [Theory]
    [InlineData(null)] // no daemon.pid at all
    [InlineData("")]
    [InlineData("not a number")]
    [InlineData("-5")]
    [InlineData("99999999")] // no such process
    public void A_missing_or_unusable_pid_file_with_the_lock_held_is_false_and_kills_nothing(string? content)
    {
        using var held = DaemonHost.TryLock(home.Config.LockPath);
        Assert.NotNull(held);
        if (content is not null) File.WriteAllText(home.Config.PidPath, content);

        Assert.False(Control().Kill());

        AssertThisProcessLives();
        Assert.DoesNotContain("killing", Log, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stand-in process (a link to `sleep` named cm-agent-hung; macOS kills a copy of a system binary at launch). Where the OS names a
    /// process after the link (Linux) it IS a cm-agent and must be ended; where it names it after the file behind it (macOS) it is not
    /// one and must be left alone. Either way the result is false here, because this test holds the daemon's lock itself.
    /// </summary>
    [Fact]
    public void A_process_is_ended_exactly_when_the_os_names_it_a_cm_agent_and_the_result_still_says_whether_the_lock_was_released()
    {
        if (OperatingSystem.IsWindows()) return;
        var sleep = new[] { "/bin/sleep", "/usr/bin/sleep" }.FirstOrDefault(File.Exists);
        if (sleep is null) return;
        var fake = Path.Combine(home.Dir, "cm-agent-hung");
        File.CreateSymbolicLink(fake, sleep);
        using var child = Process.Start(new ProcessStartInfo(fake, "120") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var isCmAgent = child.ProcessName.StartsWith("cm-agent", StringComparison.Ordinal);
            File.WriteAllText(home.Config.PidPath, child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var held = DaemonHost.TryLock(home.Config.LockPath); // the test stands in for the hung daemon's lock

            var released = Control().Kill();

            Assert.False(released, "the lock is still held by this test, so Kill must not claim it is free");
            if (isCmAgent)
            {
                Assert.True(child.WaitForExit(10_000), "a cm-agent process is ended");
                Assert.Contains($"killing the daemon (pid {child.Id})", Log, StringComparison.Ordinal);
            }
            else
            {
                Assert.False(child.HasExited, "a process that is not a cm-agent is left alone");
                Assert.DoesNotContain("killing", Log, StringComparison.Ordinal);
            }

            AssertThisProcessLives();
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }
}
