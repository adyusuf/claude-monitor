using System.Runtime.Versioning;
using Podium.Agent.Exec;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The scratch folder of a script run (ADR-0008 §3c): private, never executable, removed without following links.</summary>
public sealed class ScriptScratchTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "ap-scratch-test-" + Guid.NewGuid().ToString("N"));

    public ScriptScratchTests() => Directory.CreateDirectory(home);

    public void Dispose()
    {
        if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
    }

    [Fact]
    public void The_script_is_written_as_script_dot_extension_in_a_folder_named_by_the_run_with_a_tmp_folder()
    {
        var id = Guid.NewGuid();
        using var scratch = ScriptScratch.Create(home, id, ScriptInterpreters.Python, "print('é')\n");

        Assert.Equal(Path.Combine(home, "runs", id.ToString("N")), scratch.Folder);
        Assert.Equal(Path.Combine(scratch.Folder, "script.py"), scratch.ScriptPath);
        Assert.Equal("print('é')\n", File.ReadAllText(scratch.ScriptPath));
        Assert.True(Directory.Exists(scratch.TempFolder));
        Assert.Equal(".sh", Path.GetExtension(ScriptScratch.Create(home, Guid.NewGuid(), ScriptInterpreters.Bash, "x").ScriptPath));
        Assert.Equal(".ps1", Path.GetExtension(ScriptScratch.Create(home, Guid.NewGuid(), ScriptInterpreters.PowerShell, "x").ScriptPath));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void On_unix_the_folders_are_private_and_the_file_is_user_read_write_only_with_no_execute_bit()
    {
        if (OperatingSystem.IsWindows()) return;
        using var scratch = ScriptScratch.Create(home, Guid.NewGuid(), ScriptInterpreters.Sh, "echo\n");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(scratch.Folder));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(scratch.TempFolder));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(scratch.ScriptPath));
    }

    [Fact]
    public void A_run_id_is_used_once_and_an_unknown_interpreter_makes_nothing()
    {
        var id = Guid.NewGuid();
        using var first = ScriptScratch.Create(home, id, ScriptInterpreters.Sh, "echo\n");
        Assert.Throws<IOException>(() => ScriptScratch.Create(home, id, ScriptInterpreters.Sh, "echo\n"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScriptScratch.Create(home, Guid.NewGuid(), "perl", "x"));
        Assert.Single(Directory.GetDirectories(Path.Combine(home, "runs")));
    }

    [Theory]
    [InlineData(ScriptInterpreters.Sh, new string[0])]
    [InlineData(ScriptInterpreters.Bash, new[] { "--noprofile", "--norc" })]
    [InlineData(ScriptInterpreters.Python, new[] { "-I", "-B" })]
    [InlineData(ScriptInterpreters.PowerShell, new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File" })]
    public void The_interpreter_gets_its_flags_then_the_script_file_then_the_arguments(string interpreter, string[] flags)
    {
        using var scratch = ScriptScratch.Create(home, Guid.NewGuid(), interpreter, "x");
        Assert.Equal([.. flags, scratch.ScriptPath, "one", "two words"], scratch.Arguments(interpreter, ["one", "two words"]));
        Assert.Equal([.. flags, scratch.ScriptPath], scratch.Arguments(interpreter, null));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void The_environment_is_the_empty_one_with_a_root_owned_path_and_the_temp_folders_inside_the_scratch_folder()
    {
        if (OperatingSystem.IsWindows()) return;
        using var scratch = ScriptScratch.Create(home, Guid.NewGuid(), ScriptInterpreters.Sh, "x");

        var env = scratch.Environment(home);

        Assert.Equal("/usr/bin:/bin:/usr/sbin:/sbin", env["PATH"]);
        Assert.Equal(scratch.TempFolder, env["TMPDIR"]);
        Assert.Equal(home, env["HOME"]);
        Assert.DoesNotContain(env.Keys, k => k.StartsWith("AP_", StringComparison.Ordinal));
    }

    [Fact]
    public void Disposing_removes_the_folder_and_a_link_inside_it_is_removed_not_followed()
    {
        var outside = Path.Combine(home, "outside.txt");
        File.WriteAllText(outside, "keep me");
        var scratch = ScriptScratch.Create(home, Guid.NewGuid(), ScriptInterpreters.Sh, "x");
        if (!OperatingSystem.IsWindows()) File.CreateSymbolicLink(Path.Combine(scratch.TempFolder, "link"), outside);

        scratch.Dispose();
        scratch.Dispose(); // twice is fine

        Assert.False(Directory.Exists(scratch.Folder));
        Assert.Equal("keep me", File.ReadAllText(outside));
    }

    [Fact]
    public void The_sweep_removes_leftovers_of_runs_that_are_not_running_and_keeps_a_running_one()
    {
        var running = Guid.NewGuid();
        var left = ScriptScratch.Create(home, Guid.NewGuid(), ScriptInterpreters.Sh, "x");
        var kept = ScriptScratch.Create(home, running, ScriptInterpreters.Sh, "x");
        Directory.CreateDirectory(Path.Combine(home, "runs", "not-a-run-id"));

        var removed = ScriptScratch.Sweep(home, id => id == running);

        Assert.Equal(2, removed); // the leftover and the folder that is no run id
        Assert.False(Directory.Exists(left.Folder));
        Assert.True(Directory.Exists(kept.Folder));
        Assert.Equal(0, ScriptScratch.Sweep(Path.Combine(home, "no-such-home"), _ => false));
    }
}
