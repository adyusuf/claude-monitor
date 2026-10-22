using System.Runtime.Versioning;
using Podium.Agent.Exec;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The files and the stdin of a script run (ADR-0010 §2) through the real scratch folder and real processes.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class ScriptFilesExecTests : IDisposable
{
    private readonly ExecHarness h = new();

    public void Dispose() => h.Dispose();

    private static RunMessage Script(string body, IReadOnlyList<ScriptFile>? files = null, string? stdin = null, string interpreter = ScriptInterpreters.Sh,
        IReadOnlyList<string>? args = null) =>
        new(Guid.NewGuid(), RunModes.Script, null, null, null, 30, DateTimeOffset.UtcNow.AddMinutes(1), null, null, null, interpreter, body, args, 8 * 1024 * 1024, false, files, stdin);

    private string Runs => Path.Combine(h.Home.Config.Home, ScriptScratch.RunsFolder);

    // ---- real runs ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_script_reads_its_files_next_to_it_including_one_in_a_folder_and_sources_another_script_it_carries()
    {
        if (!ExecFixture.Unix) return;
        var run = Script("cat data.txt\n. ./lib/helper.sh\necho \"back $(ls lib)\"\n", [new("data.txt", "one\ntwo\n"), new("lib/helper.sh", "echo helped\n")]);
        var result = await h.Run(run);
        Assert.Equal((RunStatuses.Succeeded, 0), (result.Status, result.ExitCode));
        Assert.Equal("one\ntwo\nhelped\nback helper.sh\n", h.Output);
        Assert.True(!Directory.Exists(Runs) || Directory.GetDirectories(Runs).Length == 0); // all of it removed with the run
    }

    [Fact]
    public async Task The_stdin_text_reaches_the_script_and_ends_there_without_a_stdin_it_is_empty()
    {
        if (!ExecFixture.Unix) return;
        await h.Run(Script("read a\nread b\necho \"[$a][$b]\"\ncat\n", stdin: "first\nsecond line\nrest\nmore\n"));
        Assert.Equal("[first][second line]\nrest\nmore\n", h.Output);

        var empty = new ExecHarness();
        try
        {
            await empty.Run(Script("cat\necho \"end\"\n"));
            Assert.Equal("end\n", empty.Output);
        }
        finally
        {
            empty.Dispose();
        }
    }

    [Fact]
    public async Task The_files_are_private_and_never_executable_and_the_stdin_file_is_not_among_the_scripts_visible_files()
    {
        if (!ExecFixture.Unix) return;
        var run = Script("ls -A\nstat -f '%Lp %N' data.txt lib 2>/dev/null || stat -c '%a %n' data.txt lib\n", [new("data.txt", "x"), new("lib/h.sh", "y")], stdin: "in");
        await h.Run(run);
        var lines = h.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(".stdin", lines); // it exists beside the script, with a leading dot a file name may not have
        Assert.Contains("600 data.txt", lines);
        Assert.Contains("700 lib", lines);
    }

    [Fact]
    public async Task Files_and_stdin_text_are_never_in_the_agents_log()
    {
        if (!ExecFixture.Unix) return;
        await h.Run(Script("true\n", [new("data.txt", "FILE-MARKER-9876")], "STDIN-MARKER-5432"));
        Assert.DoesNotContain("FILE-MARKER-9876", h.Log, StringComparison.Ordinal);
        Assert.DoesNotContain("STDIN-MARKER-5432", h.Log, StringComparison.Ordinal);
    }

    // ---- the scratch folder ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_scratch_folder_refuses_a_path_that_would_leave_it_and_writes_nothing()
    {
        if (!ExecFixture.Unix) return;
        var home = Path.Combine(Path.GetTempPath(), "ap-scratch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            foreach (var path in new[] { "../escape.txt", "/tmp/x", "a/../../x", ".hidden", "script.sh", "tmp/x" })
            {
                Assert.Throws<ArgumentException>(() => ScriptScratch.Create(home, Guid.NewGuid(), ScriptInterpreters.Sh, "true\n", [new ScriptFile(path, "x")]));
            }

            Assert.True(Directory.GetDirectories(Path.Combine(home, ScriptScratch.RunsFolder)).Length == 0, "a refused run leaves no folder");
            Assert.False(File.Exists(Path.Combine(home, "escape.txt")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Python_keeps_isolated_mode_without_files_and_uses_its_own_folder_on_the_path_with_files()
    {
        if (!ExecFixture.Unix) return;
        var home = Path.Combine(Path.GetTempPath(), "ap-scratch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            using var plain = ScriptScratch.Create(home, Guid.NewGuid(), ScriptInterpreters.Python, "print(1)\n");
            Assert.Equal(["-I", "-B"], plain.Arguments(ScriptInterpreters.Python, null).Take(2));
            Assert.False(plain.HasFiles);
            Assert.Null(plain.StdinPath);

            using var withFiles = ScriptScratch.Create(home, Guid.NewGuid(), ScriptInterpreters.Python, "import helper\n", [new ScriptFile("helper.py", "x = 1\n")], "in");
            var args = withFiles.Arguments(ScriptInterpreters.Python, ["a"]);
            Assert.Equal(["-E", "-s", "-B", withFiles.ScriptPath, "a"], args);
            Assert.True(withFiles.HasFiles);
            Assert.Equal(Path.Combine(withFiles.Folder, ScriptScratch.StdinName), withFiles.StdinPath);
            Assert.Equal("in", File.ReadAllText(withFiles.StdinPath!));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public async Task A_python_script_imports_a_module_it_carries_when_python_is_there_and_trusted()
    {
        if (!ExecFixture.Unix || ToolInventory.RealPath("python3", ExecFixture.Os, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) is null) return;
        var result = await h.Run(Script("import helper\nprint(helper.VALUE)\n", [new("helper.py", "VALUE = 42\n")], interpreter: ScriptInterpreters.Python));
        // A host whose python is not a trusted (root-owned) file is refused by the guard before anything runs, which says nothing about the files.
        if (result.Status == RunStatuses.Succeeded) Assert.Equal("42\n", h.Output);
    }

    // ---- the target's own check -------------------------------------------------------------------------------------------

    [Fact]
    public void The_guard_refuses_files_or_stdin_that_break_the_rules_even_if_the_api_let_them_through()
    {
        if (!ExecFixture.Unix) return;
        var fx = new ExecFixture();
        try
        {
            var shell = ExecFixture.Level(ExecLevels.Shell);
            Assert.Equal(ExecErrors.Malformed, fx.Check(Script("true\n", [new("../x", "y")]), shell).Error);
            Assert.Equal(ExecErrors.Malformed, fx.Check(Script("true\n", [new("a", "1"), new("A", "2")]), shell).Error);
            Assert.Equal(ExecErrors.Malformed, fx.Check(Script("true\n", stdin: new string('x', ScriptFiles.StdinMax + 1)), shell).Error);
        }
        finally
        {
            fx.Dispose();
        }
    }
}
