using System.Runtime.Versioning;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>A script run through the real executor with real processes (ADR-0008 §3c, §3d): written to a scratch file, started by the trusted interpreter, cleaned up.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class ScriptExecTests : IDisposable
{
    private readonly ExecHarness h = new();

    public void Dispose() => h.Dispose();

    private static RunMessage Script(string body, IReadOnlyList<string>? args = null, long? cap = 8 * 1024 * 1024, int timeout = 30) =>
        new(Guid.NewGuid(), RunModes.Script, null, null, null, timeout, DateTimeOffset.UtcNow.AddMinutes(1), null, null, null, ScriptInterpreters.Sh, body, args, cap, false);

    private string Runs => Path.Combine(h.Home.Config.Home, ScriptScratch.RunsFolder);

    [Fact]
    public async Task A_script_prints_its_output_succeeds_and_leaves_no_scratch_folder_behind()
    {
        if (!ExecFixture.Unix) return;

        var result = await h.Run(Script("echo hello\n"));

        Assert.Equal((RunStatuses.Succeeded, 0, null), (result.Status, result.ExitCode, result.Error));
        Assert.Equal("hello\n", h.Output);
        Assert.Equal([RunStatuses.Running, RunStatuses.Succeeded], h.Statuses.Select(s => s.Status));
        Assert.True(!Directory.Exists(Runs) || Directory.GetDirectories(Runs).Length == 0);
    }

    [Fact]
    public async Task The_arguments_reach_the_script_unchanged_including_spaces()
    {
        if (!ExecFixture.Unix) return;
        await h.Run(Script("echo \"[$1][$2]\"\n", ["a b", "c;d"]));
        Assert.Equal("[a b][c;d]\n", h.Output);
    }

    [Fact]
    public async Task The_script_runs_in_its_own_scratch_folder_with_a_fixed_path_and_a_tmpdir_inside_it_and_sees_nothing_of_the_agent()
    {
        if (!ExecFixture.Unix) return;
        Environment.SetEnvironmentVariable("CM_TEST_LEAK", "leaked");
        try
        {
            var run = Script("pwd -P\necho \"$PATH\"\necho \"$TMPDIR\"\necho \"leak=${CM_TEST_LEAK:-none}\"\n");
            var result = await h.Run(run);
            Assert.Equal(RunStatuses.Succeeded, result.Status);
            var lines = h.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var folder = Path.Combine(Path.GetFullPath(h.Home.Config.Home), ScriptScratch.RunsFolder, run.Id.ToString("N"));
            Assert.EndsWith(Path.Combine(ScriptScratch.RunsFolder, run.Id.ToString("N")), lines[0], StringComparison.Ordinal);
            Assert.Equal("/usr/bin:/bin:/usr/sbin:/sbin", lines[1]);
            Assert.EndsWith(Path.Combine(run.Id.ToString("N"), "tmp"), lines[2], StringComparison.Ordinal);
            Assert.Equal("leak=none", lines[3]);
            Assert.False(Directory.Exists(folder));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CM_TEST_LEAK", null);
        }
    }

    [Fact]
    public async Task A_failing_script_reports_its_exit_code_and_is_cleaned_up_too()
    {
        if (!ExecFixture.Unix) return;
        var result = await h.Run(Script("echo oops >&2\nexit 3\n"));
        Assert.Equal((RunStatuses.Failed, 3), (result.Status, result.ExitCode));
        Assert.True(!Directory.Exists(Runs) || Directory.GetDirectories(Runs).Length == 0);
    }

    [Fact]
    public async Task The_script_text_is_never_in_the_agents_log_only_the_interpreter_name()
    {
        if (!ExecFixture.Unix) return;
        await h.Run(Script("echo 'SECRET-MARKER-1234'\n"));
        Assert.DoesNotContain("SECRET-MARKER-1234", h.Log, StringComparison.Ordinal);
        Assert.Contains("mode=script", h.Log, StringComparison.Ordinal);
        Assert.Contains("exe=sh", h.Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_is_masked_like_every_runs()
    {
        if (!ExecFixture.Unix) return;
        await h.Run(Script("echo ghp_" + new string('b', 36) + "\n"));
        Assert.Equal("[masked:github_token]\n", h.Output);
    }

    [Fact]
    public async Task A_script_that_prints_more_than_its_cap_keeps_a_head_and_a_tail_and_the_final_status_says_how_much_was_dropped()
    {
        if (!ExecFixture.Unix) return;
        const long cap = 1024 * 1024;
        var result = await h.Run(Script("head -c 3145728 /dev/zero | tr '\\000' 'x'\n", cap: cap, timeout: 60));

        Assert.Equal(RunStatuses.Succeeded, result.Status);
        Assert.True(result.OutputTruncated);
        Assert.True(result.BytesDropped > 0);
        var final = h.Statuses.Last();
        Assert.Equal((result.BytesRead, result.BytesDropped), (final.OutputReadBytes, final.OutputDroppedBytes));
        Assert.Equal(result.Chunks, final.OutputChunks);
        Assert.Equal(h.Chunks.Count, final.OutputChunks);
        Assert.True(h.Chunks.Sum(c => (long)System.Text.Encoding.UTF8.GetByteCount(c.Body)) <= cap, "what is kept stays within the cap");
        Assert.Contains(h.Chunks, c => c.GapBefore); // the cut is marked where it happened
    }

    [Fact]
    public async Task A_script_that_fits_its_cap_loses_nothing()
    {
        if (!ExecFixture.Unix) return;
        var result = await h.Run(Script("head -c 100000 /dev/zero | tr '\\000' 'y'\n", cap: 1024 * 1024));
        Assert.Equal((false, 0L), (result.OutputTruncated, result.BytesDropped));
        Assert.DoesNotContain(h.Chunks, c => c.GapBefore);
    }

    [Fact]
    public async Task The_agents_own_ceiling_caps_what_the_workspace_asked_for()
    {
        if (!ExecFixture.Unix) return;
        using var small = new ExecHarness(c => c with { ExecScriptOutputCeiling = 1024 * 1024 });
        var result = await small.Run(Script("head -c 3145728 /dev/zero | tr '\\000' 'x'\n", cap: 64L * 1024 * 1024, timeout: 60));
        Assert.True(result.BytesDropped > 0);
    }

    [Fact]
    public async Task A_run_without_a_cap_uses_eight_mebibytes()
    {
        if (!ExecFixture.Unix) return;
        var result = await h.Run(Script("head -c 2097152 /dev/zero | tr '\\000' 'x'\n", cap: null, timeout: 60));
        Assert.Equal(0L, result.BytesDropped);
    }

    [Fact]
    public async Task A_script_over_its_timeout_is_killed_and_its_scratch_folder_goes()
    {
        if (!ExecFixture.Unix) return;
        var result = await h.Run(Script("sleep 30\n", timeout: 1));
        Assert.Equal(RunStatuses.TimedOut, result.Status);
        Assert.True(!Directory.Exists(Runs) || Directory.GetDirectories(Runs).Length == 0);
    }

    [Fact]
    public async Task The_guard_refuses_a_script_under_a_ceiling_before_anything_is_written()
    {
        if (!ExecFixture.Unix) return;
        using var capped = new ExecHarness(guard: run => ExecGuard.Check(run, new ExecPolicy(ExecLevels.Shell, ["/usr/bin/true"], []), ExecFixture.Os, "/home"));
        var result = await capped.Run(Script("echo hello\n"));
        Assert.Equal((RunStatuses.Failed, ExecErrors.ExecLevel), (result.Status, result.Error));
        Assert.False(Directory.Exists(Path.Combine(capped.Home.Config.Home, ScriptScratch.RunsFolder)));
    }
}
