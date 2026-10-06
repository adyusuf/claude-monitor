using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Update;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The real process runner and the detached "update --auto" start, against /bin/sh scripts in a throw-away folder (never a harness).</summary>
public sealed class ProcessRunnerTests : IDisposable
{
    private readonly TempHome home = new();

    public void Dispose() => home.Dispose();

    private string Script(string name, string body)
    {
        var path = Path.Combine(home.Dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public void It_returns_the_exit_code_and_what_the_program_printed()
    {
        if (OperatingSystem.IsWindows()) return;
        var tool = Script("tool", "echo \"$1\"; echo ignored-error >&2; exit 3");
        var (exit, output) = new SystemProcessRunner().Run(tool, ["hello"], TimeSpan.FromSeconds(30));
        Assert.Equal(3, exit);
        Assert.Equal("hello\n", output);
    }

    [Fact]
    public void A_program_that_cannot_be_started_is_minus_one()
    {
        Assert.Equal((-1, ""), new SystemProcessRunner().Run(Path.Combine(home.Dir, "missing"), [], TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_program_that_does_not_end_in_time_is_killed_and_minus_one()
    {
        if (OperatingSystem.IsWindows()) return;
        var tool = Script("slow", "exec sleep 30");
        Assert.Equal((-1, ""), new SystemProcessRunner().Run(tool, [], TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task The_update_is_started_as_update_auto_from_the_installed_binary()
    {
        if (OperatingSystem.IsWindows()) return;
        var marker = Path.Combine(home.Dir, "marker");
        Directory.CreateDirectory(Path.GetDirectoryName(home.Config.BinaryPath)!);
        var script = Script("placeholder", $"echo \"$@\" > \"{marker}\"");
        File.Move(script, home.Config.BinaryPath, overwrite: true);

        Assert.True(DaemonControl.SpawnAutoUpdate(home.Config, new AgentLog(home.Config, TimeProvider.System)));

        await Until.True(() => File.Exists(marker) && new FileInfo(marker).Length > 0);
        Assert.Equal("update --auto", (await File.ReadAllTextAsync(marker)).Trim());
    }

    [Fact]
    public async Task A_missing_binary_is_logged_and_reported_as_not_started()
    {
        Assert.False(DaemonControl.SpawnAutoUpdate(home.Config, new AgentLog(home.Config, TimeProvider.System)));
        Assert.Contains("could not start the update", await File.ReadAllTextAsync(home.Config.LogPath), StringComparison.Ordinal);
    }
}
