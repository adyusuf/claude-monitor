using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Resource limits of runs on macOS (ADR-0005): the launcher script is checked everywhere, real children only on macOS.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class RunLimitsTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> Env = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" };
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private readonly TempHome home = new();
    private readonly string dir = Path.Combine(Path.GetTempPath(), "cm-limits-" + Guid.NewGuid().ToString("N"));

    public RunLimitsTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        home.Dispose();
        Directory.Delete(dir, recursive: true);
    }

    private static readonly RunLimits Roomy = new(CpuSeconds: 100, FileBytes: 1024L * 1024 * 1024, OpenFiles: 512);

    private static async Task<(string Out, RunExit Exit)> RunAsync(IRunProcess process)
    {
        await using (process)
        {
            using var reader = new StreamReader(process.Stdout, Encoding.UTF8);
            var text = reader.ReadToEndAsync();
            var exit = await process.Exited.WaitAsync(Wait);
            return (await text.WaitAsync(Wait), exit);
        }
    }

    private Task<(string Out, RunExit Exit)> ShellAsync(string text, RunLimits limits) =>
        RunAsync(ProcessTree.StartShell(text, dir, Env, limits: limits));

    [Fact]
    public void The_launcher_passes_the_target_its_name_and_arguments_as_positional_parameters_never_as_script_text()
    {
        var (exe, argv) = RunLauncher.Wrap("/usr/bin/tool", ["name", "a b", "$(rm -rf x)", "'; exit 0 #"], new RunLimits(120, 2049, 300));

        Assert.Equal("/bin/sh", exe);
        Assert.Equal(["sh", "-c"], argv.Take(2));
        Assert.Equal(["/usr/bin/tool", "name", "a b", "$(rm -rf x)", "'; exit 0 #"], argv.Skip(3));
        Assert.Equal("ulimit -t 120 -f 3 -c 0 -n 300 || exit 126; unset PWD; a=$1; shift; exec -a \"$a\" -- \"$0\" \"$@\"", argv[2]);
    }

    [Theory]
    [InlineData(1L, 1)]
    [InlineData(1024L, 1)]
    [InlineData(1025L, 2)]
    [InlineData(1073741824L, 1048576)]
    public void The_file_size_limit_is_counted_in_blocks_of_1024_bytes_rounded_up(long bytes, long blocks)
    {
        var (_, argv) = RunLauncher.Wrap("/bin/x", ["x"], new RunLimits(1, bytes, 64));
        Assert.Contains($"-f {blocks} -c 0", argv[2], StringComparison.Ordinal);
    }

    [Fact]
    public void A_launch_without_argv_zero_is_refused()
    {
        Assert.Throws<ArgumentException>(() => RunLauncher.Wrap("/bin/x", [], Roomy));
        Assert.Throws<ArgumentException>(() => RunLauncher.Wrap("", ["x"], Roomy));
    }

    [Fact]
    public void The_limits_follow_the_configuration_and_stay_inside_their_bounds()
    {
        var config = home.Config with { ExecKillGrace = TimeSpan.FromSeconds(5), ExecCpuCores = 4, ExecFileSizeMax = 10_000_000, ExecOpenFilesMax = 2000 };
        Assert.Equal(new RunLimits((120 + 5) * 4, 10_000_000, 2000), RunLimits.For(config, TimeSpan.FromSeconds(120)));

        var tiny = RunLimits.For(config with { ExecCpuCores = -5, ExecFileSizeMax = 0, ExecOpenFilesMax = 3 }, TimeSpan.FromSeconds(1));
        Assert.Equal(new RunLimits(6, RunLimits.MinFileBytes, RunLimits.MinOpenFiles), tiny);

        var huge = RunLimits.For(config with { ExecCpuCores = 100_000, ExecOpenFilesMax = int.MaxValue }, TimeSpan.FromSeconds(3600));
        Assert.Equal((3605 * RunLimits.MaxCpuCores, RunLimits.MaxOpenFiles), (huge.CpuSeconds, huge.OpenFiles));
    }

    [Fact]
    public void The_defaults_are_a_gibibyte_of_file_a_thousand_descriptors_and_one_core_budget_per_processor()
    {
        Assert.Equal((1024L * 1024 * 1024, 1024, Environment.ProcessorCount), (home.Config.ExecFileSizeMax, home.Config.ExecOpenFilesMax, home.Config.ExecCpuCores));
    }

    [Fact]
    public async Task Arguments_arrive_byte_identical_through_the_launcher_whatever_they_hold()
    {
        if (!OperatingSystem.IsMacOS()) return; // the launcher is macOS only (RunLimits.AppliesHere)
        string[] args = ["a b", "q'uo\"te", "$(echo hi)", "*", "new\nline", "-x", "", "\\n", "`id`", "$HOME", "--", "é ü 日本"];
        var (output, exit) = await RunAsync(ProcessTree.Start("/usr/bin/printf", ["[%s]", .. args], dir, Env, limits: Roomy));

        Assert.Equal(0, exit.Code);
        Assert.Equal(string.Concat(args.Select(a => $"[{a}]")), output);
    }

    [Fact]
    public async Task The_target_keeps_the_name_it_was_given_and_the_launcher_leaves_no_working_directory_in_the_environment()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var (name, _) = await RunAsync(ProcessTree.Start("/bin/sh", ["-c", "echo $0"], dir, Env, argv0: "named", limits: Roomy));
        Assert.Equal("named\n", name);

        var (env, _) = await RunAsync(ProcessTree.Start("/usr/bin/env", [], dir, Env, limits: Roomy));
        Assert.Equal(["PATH=/usr/bin:/bin", "SHLVL=0"], env.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order());
    }

    [Fact]
    public async Task The_limits_are_in_force_inside_the_run_and_cannot_be_raised_by_it()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var limits = new RunLimits(CpuSeconds: 777, FileBytes: 3 * 1024 * 1024, OpenFiles: 300);
        var (output, exit) = await ShellAsync("echo $(ulimit -t) $(ulimit -f) $(ulimit -c) $(ulimit -n); ulimit -n 5000 2>/dev/null && echo raised; ulimit -c unlimited 2>/dev/null && echo raised-core", limits);

        Assert.Equal(1, exit.Code); // the failed attempt to raise: the hard limit stands
        Assert.Equal("777 3072 0 300\n", output);
    }

    [Fact]
    public async Task A_run_that_writes_past_the_file_size_limit_is_stopped_by_the_signal_and_leaves_a_file_of_the_limit()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var limits = Roomy with { FileBytes = 1024 * 1024 };
        var (_, exit) = await ShellAsync("exec /bin/dd if=/dev/zero of=big bs=1024 count=4096 2>/dev/null", limits);

        Assert.Equal(25, exit.Signal); // SIGXFSZ
        Assert.Equal(1024 * 1024, new FileInfo(Path.Combine(dir, "big")).Length);
    }

    [Fact]
    public async Task A_run_that_burns_more_cpu_than_its_budget_is_stopped_by_the_signal()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var (_, exit) = await ShellAsync("exec /usr/bin/perl -e '1 while 1'", Roomy with { CpuSeconds = 1 });
        Assert.Equal(24, exit.Signal); // SIGXCPU
    }

    [Fact]
    public async Task A_run_never_dumps_core()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var (_, exit) = await ShellAsync("exec /bin/sh -c 'kill -SEGV $$'", Roomy);
        Assert.Equal(11, exit.Signal);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task Exit_codes_and_signals_of_the_target_are_reported_as_without_the_launcher()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(new RunExit(0, null), (await RunAsync(ProcessTree.Start("/usr/bin/true", [], dir, Env, limits: Roomy))).Exit);
        Assert.Equal(new RunExit(1, null), (await RunAsync(ProcessTree.Start("/usr/bin/false", [], dir, Env, limits: Roomy))).Exit);
        Assert.Equal(new RunExit(7, null), (await ShellAsync("exit 7", Roomy)).Exit);
        Assert.Equal(new RunExit(126, null), (await ShellAsync("exit 126", Roomy)).Exit);
        Assert.Equal(new RunExit(137, 9), (await ShellAsync("kill -9 $$", Roomy)).Exit);
        Assert.Equal(new RunExit(143, 15), (await ShellAsync("kill -TERM $$", Roomy)).Exit);
    }

    [Fact]
    public async Task A_limit_the_system_refuses_fails_the_run_before_the_target_starts()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var marker = Path.Combine(dir, "started");
        var (_, exit) = await RunAsync(ProcessTree.Start("/usr/bin/touch", [marker], dir, Env, limits: Roomy with { CpuSeconds = -1 }));

        Assert.Equal(126, exit.Code);
        Assert.False(File.Exists(marker), "the target ran without its limits");
    }

    [Fact]
    public void A_program_that_is_missing_or_not_executable_cannot_be_started_as_without_the_launcher()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Throws<Win32Exception>(() => ProcessTree.Start(Path.Combine(dir, "nope"), [], dir, Env, limits: Roomy));
        var plain = ExecFixture.File(dir, "plain.txt");
        Assert.Throws<Win32Exception>(() => ProcessTree.Start(plain, [], dir, Env, limits: Roomy));
        Assert.Throws<Win32Exception>(() => ProcessTree.Start(dir, [], dir, Env, limits: Roomy));
    }

    [Fact]
    public async Task The_executor_starts_runs_with_the_configured_limits_and_the_kill_still_ends_the_launched_process()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var h = new ExecHarness(c => c with { ExecOpenFilesMax = 300, ExecFileSizeMax = 8192 });
        var result = await h.Run(ExecFixture.Shell("echo $(ulimit -n) $(ulimit -f)"));
        Assert.Equal((RunStatuses.Succeeded, "300 8\n"), (result.Status, h.Output));

        using var cancel = new CancellationTokenSource();
        var pidFile = Path.Combine(h.Fx.Dir, "child.pid");
        var run = h.Run(ExecFixture.Shell($"echo $$ > {pidFile}; exec sleep 300", timeout: 600), cancel.Token);
        var pid = await ExecHarness.ReadPidAsync(pidFile);
        await cancel.CancelAsync();
        Assert.Equal(RunStatuses.Cancelled, (await run.WaitAsync(Wait)).Status);
        Assert.True(await ExecHarness.UntilAsync(() => !ExecHarness.IsAlive(pid)), $"process {pid} survived the cancel");
    }
}
