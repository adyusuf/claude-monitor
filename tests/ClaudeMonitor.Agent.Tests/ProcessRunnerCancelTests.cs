using System.Diagnostics;
using ClaudeMonitor.Agent.Update;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The real runner's cancellable overload, against /bin/sh scripts in a throw-away folder: stopped on cancel and on timeout, telling how each run ended.</summary>
public sealed class ProcessRunnerCancelTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    private readonly TempHome home = new();

    public void Dispose() => home.Dispose();

    private string Script(string name, string body)
    {
        var path = Path.Combine(home.Dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private string Path_(string name) => Path.Combine(home.Dir, name);

    private static bool Running(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private int PidIn(string file) => int.Parse(File.ReadAllText(Path_(file)).Trim(), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A script that writes its pid and its child's, prints something, then waits on a long sleep.</summary>
    private string Sleeper() => Script("sleeper", $"echo $$ > '{Path_("pid")}'\nsleep 30 &\necho $! > '{Path_("child")}'\necho secret-output\nwait");

    [Fact]
    public async Task Cancelling_ends_the_whole_process_tree_and_says_cancelled()
    {
        if (OperatingSystem.IsWindows()) return;
        var tool = Sleeper();
        using var stop = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        var run = Task.Run(() => new SystemProcessRunner().Run(tool, [], TimeSpan.FromMinutes(5), stop.Token));

        await Until.True(() => File.Exists(Path_("child")) && new FileInfo(Path_("child")).Length > 0);
        await stop.CancelAsync();
        var result = await run.WaitAsync(Bound);

        Assert.Equal((ProcessEnd.Cancelled, -1, "", null), (result.End, result.ExitCode, result.Output, result.Error));
        Assert.True(clock.Elapsed < Bound, $"it took {clock.Elapsed}");
        await Until.True(() => !Running(PidIn("pid")));
        await Until.True(() => !Running(PidIn("child"))); // the install's own children are not left behind
    }

    [Fact]
    public async Task A_run_that_does_not_end_in_time_is_killed_and_says_timed_out_without_its_output()
    {
        if (OperatingSystem.IsWindows()) return;
        var tool = Sleeper();
        var clock = Stopwatch.StartNew();

        var result = await Task.Run(() => new SystemProcessRunner().Run(tool, [], TimeSpan.FromMilliseconds(500), CancellationToken.None)).WaitAsync(Bound);

        Assert.Equal((ProcessEnd.TimedOut, -1, "", null), (result.End, result.ExitCode, result.Output, result.Error)); // it printed "secret-output", the result does not repeat it
        Assert.True(clock.Elapsed < Bound, $"it took {clock.Elapsed}");
        await Until.True(() => !Running(PidIn("pid")));
        await Until.True(() => !Running(PidIn("child")));
    }

    [Fact]
    public void A_cancel_wins_over_a_longer_timeout_and_a_timeout_over_a_token_that_is_never_cancelled()
    {
        if (OperatingSystem.IsWindows()) return;
        var tool = Script("slow", "exec sleep 30");
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        Assert.Equal(ProcessEnd.Cancelled, new SystemProcessRunner().Run(tool, [], TimeSpan.FromMinutes(5), stop.Token).End);
        using var never = new CancellationTokenSource();
        Assert.Equal(ProcessEnd.TimedOut, new SystemProcessRunner().Run(tool, [], TimeSpan.FromMilliseconds(500), never.Token).End);
    }

    [Fact]
    public void A_token_cancelled_before_the_run_starts_nothing()
    {
        if (OperatingSystem.IsWindows()) return;
        var tool = Script("marker", $"echo started > '{Path_("started")}'");
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var result = new SystemProcessRunner().Run(tool, [], TimeSpan.FromSeconds(30), stop.Token);

        Assert.Equal((ProcessEnd.Cancelled, -1, ""), (result.End, result.ExitCode, result.Output));
        Assert.False(File.Exists(Path_("started")));
    }

    [Fact]
    public void A_process_that_ends_by_itself_is_exited_with_its_code_and_output_whatever_the_token()
    {
        if (OperatingSystem.IsWindows()) return;
        var tool = Script("tool", "echo \"$1\"; echo ignored-error >&2; exit 3");
        using var stop = new CancellationTokenSource();

        var result = new SystemProcessRunner().Run(tool, ["hello"], TimeSpan.FromSeconds(30), stop.Token);

        Assert.Equal((ProcessEnd.Exited, 3, "hello\n", null), (result.End, result.ExitCode, result.Output, result.Error));
    }

    [Fact]
    public void A_program_that_cannot_be_started_is_not_started_and_names_the_error_type_only()
    {
        var result = new SystemProcessRunner().Run(Path.Combine(home.Dir, "missing"), [], TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal((ProcessEnd.NotStarted, -1, ""), (result.End, result.ExitCode, result.Output));
        Assert.Equal("Win32Exception", result.Error);
    }

    [Fact]
    public void The_plain_overload_reports_a_cancelled_or_timed_out_run_as_minus_one_and_an_exit_as_its_code()
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new SystemProcessRunner();
        Assert.Equal((-1, ""), runner.Run(Script("slow", "exec sleep 30"), [], TimeSpan.FromMilliseconds(300)));
        Assert.Equal((0, "ok\n"), runner.Run(Script("ok", "echo ok"), [], TimeSpan.FromSeconds(30)));
    }
}
