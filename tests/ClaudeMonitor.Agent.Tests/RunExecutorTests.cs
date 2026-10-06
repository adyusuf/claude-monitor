using System.Runtime.Versioning;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class RunExecutorTests : IDisposable
{
    private readonly ExecHarness h = new();

    public void Dispose() => h.Dispose();

    [Fact]
    public async Task A_program_that_exits_zero_succeeds_with_its_output_and_reports_running_then_one_final_status()
    {
        if (!ExecFixture.Unix) return;
        var result = await h.Run(ExecFixture.Argv(["/bin/echo", "hello"]));

        Assert.Equal((RunStatuses.Succeeded, 0, null), (result.Status, result.ExitCode, result.Error));
        Assert.Equal("hello\n", h.Output);
        Assert.Equal([RunStatuses.Running, RunStatuses.Succeeded], h.Statuses.Select(s => s.Status));
        Assert.Equal(ExecPaths.RealPath("/bin/echo", ExecFixture.Os, false), h.Statuses.First().ResolvedExe);
        Assert.Equal(6, result.BytesRead);
        Assert.False(result.OutputTruncated);
    }

    [Fact]
    public async Task A_non_zero_exit_is_a_failed_run_carrying_the_exit_code()
    {
        if (!ExecFixture.Unix) return;
        var result = await h.Run(ExecFixture.Shell("echo oops >&2; exit 3"));
        Assert.Equal((RunStatuses.Failed, 3, null), (result.Status, result.ExitCode, result.Error));
        var chunk = Assert.Single(h.Chunks);
        Assert.Equal((RunStreams.Stderr, "oops\n"), (chunk.Stream, chunk.Body));
    }

    [Fact]
    public async Task A_program_killed_by_a_signal_fails_with_the_signal_named()
    {
        if (!ExecFixture.Unix) return;
        var result = await h.Run(ExecFixture.Shell("kill -9 $$"));
        Assert.Equal((RunStatuses.Failed, 137, "signal_9"), (result.Status, result.ExitCode, result.Error));
    }

    [Fact]
    public async Task Output_is_masked_whatever_the_workspace_setting()
    {
        if (!ExecFixture.Unix) return;
        var token = "ghp_" + new string('b', 36);
        await h.Run(ExecFixture.Argv(["/bin/echo", token]));
        Assert.Equal("[masked:github_token]\n", h.Output);
    }

    [Fact]
    public async Task The_run_sees_only_a_fixed_path_the_home_and_a_locale_and_nothing_from_the_agent()
    {
        if (!ExecFixture.Unix) return;
        Environment.SetEnvironmentVariable("CM_TEST_LEAK", "leaked");
        Environment.SetEnvironmentVariable("DOTNET_TEST_LEAK", "leaked");
        try
        {
            var result = await h.Run(ExecFixture.Argv(["/usr/bin/env"]));
            Assert.Equal(RunStatuses.Succeeded, result.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CM_TEST_LEAK", null);
            Environment.SetEnvironmentVariable("DOTNET_TEST_LEAK", null);
        }

        var lines = h.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order().ToList();
        Assert.Equal([$"HOME={h.Home.Config.Home}", "LANG=C.UTF-8", "LC_ALL=C.UTF-8", "PATH=/usr/bin:/bin"], lines);
    }

    [Fact]
    public async Task Standard_input_is_the_null_device_so_a_reader_ends_at_once_and_no_other_descriptor_is_inherited()
    {
        if (!ExecFixture.Unix) return;
        var cat = await h.Run(ExecFixture.Argv(["/bin/cat"], timeout: 20));
        Assert.Equal((RunStatuses.Succeeded, ""), (cat.Status, h.Output));
        Assert.True(cat.Duration < TimeSpan.FromSeconds(10));

        // ls itself holds a directory handle or two (3, 4 on macOS); anything above is a descriptor the agent leaked into the run.
        // This test process has many descriptors open (database, pipes, sockets), so a leak would show up well above 4.
        using var fds = new ExecHarness();
        await fds.Run(ExecFixture.Shell("ls /dev/fd"));
        var open = fds.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Order().ToList();
        Assert.Contains(2, open);
        Assert.All(open, fd => Assert.True(fd <= 4, $"descriptor {fd} was inherited"));
    }

    [Fact]
    public async Task A_grant_working_directory_is_used_when_the_run_names_none_and_otherwise_the_home_is()
    {
        if (!ExecFixture.Unix) return;
        var grant = new GrantTemplate(["/bin/pwd"], h.Fx.Root, 60);
        var granted = await h.Run(ExecFixture.Argv(["/bin/pwd"], grant));
        Assert.Equal(RunStatuses.Succeeded, granted.Status);
        Assert.Equal(ExecPaths.RealPath(h.Fx.Root, ExecFixture.Os, false) + "\n", h.Output);

        using var plain = new ExecHarness();
        await plain.Run(ExecFixture.Argv(["/bin/pwd"]));
        Assert.Equal(ExecPaths.RealPath(plain.Home.Config.Home, ExecFixture.Os, false) + "\n", plain.Output);
    }

    [Fact]
    public async Task A_shell_run_works_in_the_service_home()
    {
        if (!ExecFixture.Unix) return;
        await h.Run(ExecFixture.Shell("pwd"));
        Assert.Equal(ExecPaths.RealPath(h.Home.Config.Home, ExecFixture.Os, false) + "\n", h.Output);
    }

    [Fact]
    public async Task The_log_holds_run_facts_but_no_argument_no_shell_text_and_no_output()
    {
        if (!ExecFixture.Unix) return;
        var grantId = Guid.NewGuid();
        var run = ExecFixture.Argv(["/bin/echo", "ARGVALUE-4711", "OUTPUTWORD-99"], grantId: null) with { GrantId = grantId };
        using var granted = new ExecHarness(guard: r => new ExecDecision(true, null, "/bin/echo"));
        var result = await granted.Run(run);
        await h.Run(ExecFixture.Shell("echo SHELLTEXT-8080"));

        Assert.Equal(RunStatuses.Succeeded, result.Status);
        var log = granted.Log;
        Assert.Contains($"run start id={run.Id} grant={grantId}", log, StringComparison.Ordinal);
        Assert.Contains("exe=/bin/echo", log, StringComparison.Ordinal);
        Assert.Contains($"run end id={run.Id}", log, StringComparison.Ordinal);
        Assert.Contains("status=succeeded exit=0", log, StringComparison.Ordinal);
        Assert.DoesNotContain("ARGVALUE", log, StringComparison.Ordinal);
        Assert.DoesNotContain("OUTPUTWORD", log, StringComparison.Ordinal);
        Assert.DoesNotContain("SHELLTEXT", h.Log, StringComparison.Ordinal);
        Assert.Contains("mode=shell", h.Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_name_with_a_line_break_cannot_forge_a_log_line()
    {
        if (!ExecFixture.Unix) return;
        using var forged = new ExecHarness(guard: r => new ExecDecision(true, null, "/bin/echo"));
        await forged.Run(ExecFixture.Argv(["/bin/echo\nFORGED run end id=x status=succeeded"]));
        var log = forged.Log;
        Assert.Contains("FORGED", log, StringComparison.Ordinal);
        Assert.DoesNotContain("\nFORGED", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_program_that_cannot_be_started_fails_as_spawn_failed()
    {
        if (!ExecFixture.Unix) return;
        using var broken = new ExecHarness(guard: r => new ExecDecision(true, null, "/nonexistent/program"));
        var result = await broken.Run(ExecFixture.Argv(["/nonexistent/program"]));
        Assert.Equal((RunStatuses.Failed, RunFailures.SpawnFailed), (result.Status, result.Error));
        Assert.DoesNotContain(broken.Statuses, s => s.Status == RunStatuses.Running);
    }
}
