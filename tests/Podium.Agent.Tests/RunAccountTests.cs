using System.Runtime.Versioning;
using Podium.Agent.Exec;
using Podium.Agent.Install;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The separate run account (ADR-0010 §5): the command line, the shared scratch folder, the policy file and the installer steps (with fakes: no account, folder or sudoers file of this machine is touched).</summary>
[UnsupportedOSPlatform("windows")]
public sealed class RunAccountTests
{
    [Theory]
    [InlineData("ap-run", true)]
    [InlineData("_aprun", true)]
    [InlineData("runner_1", true)]
    [InlineData("", false)]
    [InlineData("root", false)]
    [InlineData("ROOT", false)]
    [InlineData("wheel", false)]
    [InlineData("admin", false)]
    [InlineData("nobody", false)]
    [InlineData("1abc", false)]
    [InlineData("-abc", false)]
    [InlineData("a b", false)]
    [InlineData("a;b", false)]
    [InlineData("a/b", false)]
    [InlineData("a$b", false)]
    [InlineData("a\nb", false)]
    public void Only_a_plain_unprivileged_name_is_a_run_account(string name, bool valid) => Assert.Equal(valid, RunAccount.IsValidName(name));

    [Fact]
    public void A_name_longer_than_32_characters_and_null_are_not_accounts_and_the_defaults_are_per_os()
    {
        Assert.False(RunAccount.IsValidName(new string('a', 33)));
        Assert.True(RunAccount.IsValidName(new string('a', 32)));
        Assert.False(RunAccount.IsValidName(null));
        Assert.Equal(("ap-run", "_aprun", null), (RunAccount.DefaultName(OsKinds.Linux), RunAccount.DefaultName(OsKinds.MacOs), RunAccount.DefaultName(OsKinds.Windows)));
    }

    [Fact]
    public void A_run_starts_through_sudo_as_the_account_with_exactly_the_given_environment_and_no_text_through_the_shell()
    {
        var env = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["HOME"] = "/srv/run-work", ["LANG"] = "C.UTF-8" };
        var (exe, argv) = RunAccount.Wrap("ap-run", "/usr/bin/tail", ["tail", "-n", "5", "/var/log/a b.log"], env);

        Assert.Equal(RunAccount.Sudo, exe);
        Assert.Equal([RunAccount.Sudo, "-n", "-u", "ap-run", "--", RunAccount.Env, "-i", "PATH=/usr/bin:/bin", "HOME=/srv/run-work", "LANG=C.UTF-8",
            RunAccount.Sh, "-c", "umask 007; exec \"$@\"", "sh", "/usr/bin/tail", "-n", "5", "/var/log/a b.log"], argv);
        Assert.DoesNotContain(argv, a => a.Contains(';', StringComparison.Ordinal) && a != "umask 007; exec \"$@\""); // the only text the shell gets is its own fixed one
    }

    [Fact]
    public void A_shell_run_keeps_its_text_as_one_argument_for_the_run_accounts_own_shell()
    {
        var (_, argv) = RunAccount.Wrap("ap-run", "/bin/sh", ["sh", "-c", "echo a; echo b"], new Dictionary<string, string> { ["PATH"] = "/usr/bin" });
        Assert.Equal(["/bin/sh", "-c", "echo a; echo b"], argv.TakeLast(3));
    }

    [Fact]
    public void A_name_that_is_not_an_account_is_refused_before_any_command_is_built_and_sudo_itself_gets_only_a_path()
    {
        Assert.Throws<ArgumentException>(() => RunAccount.Wrap("root", "/bin/true", ["true"], new Dictionary<string, string>()));
        Assert.Throws<ArgumentException>(() => RunAccount.Wrap("a;b", "/bin/true", ["true"], new Dictionary<string, string>()));
        Assert.Equal(["PATH"], RunAccount.SudoEnvironment.Keys);
    }

    [Fact]
    public void The_sudoers_text_lets_one_account_act_as_one_account_and_nothing_else()
    {
        var text = RunAccount.SudoersText("aipod", "ap-run");
        Assert.Contains("aipod ALL=(ap-run) NOPASSWD: ALL", text, StringComparison.Ordinal);
        Assert.Contains("Defaults:aipod !requiretty", text, StringComparison.Ordinal);
        Assert.Contains("Defaults:aipod !use_pty", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ALL=(ALL", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(root)", text, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => RunAccount.SudoersText("aipod", "root"));
        Assert.Throws<ArgumentException>(() => RunAccount.SudoersText("bad name", "ap-run"));
        Assert.Throws<ArgumentException>(() => RunAccount.SudoersText("aipod\nALL ALL=(ALL) ALL", "ap-run"));
    }

    [Fact]
    public void The_work_folder_is_below_the_home()
    {
        Assert.Equal(Path.Combine("/var/lib/aipod", "run-work"), RunAccount.WorkFolder("/var/lib/aipod"));
    }

    // ---- the scratch folder it shares ------------------------------------------------------------------------------------------

    [Fact]
    public void A_shared_scratch_folder_is_made_directly_in_the_work_folder_with_group_access_and_the_stdin_and_files_follow()
    {
        var work = Path.Combine(Path.GetTempPath(), "ap-work-" + Guid.NewGuid().ToString("N"));
        try
        {
            var id = Guid.NewGuid();
            using (var scratch = ScriptScratch.Create(work, id, ScriptInterpreters.Sh, "true\n", [new ScriptFile("lib/a.txt", "x")], "in", shared: true))
            {
                Assert.Equal(Path.Combine(work, id.ToString("N")), scratch.Folder);
                const UnixFileMode group = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | group, File.GetUnixFileMode(scratch.Folder) & (UnixFileMode)0b111_111_111);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite, File.GetUnixFileMode(scratch.ScriptPath) & (UnixFileMode)0b111_111_111);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | group, File.GetUnixFileMode(Path.Combine(scratch.Folder, "lib")) & (UnixFileMode)0b111_111_111);
                Assert.Equal(0, (int)(File.GetUnixFileMode(scratch.StdinPath!) & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)));
            }

            Assert.False(Directory.Exists(Path.Combine(work, id.ToString("N"))));
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void A_scratch_folder_without_a_run_account_keeps_its_old_private_mode_and_place()
    {
        var home = Path.Combine(Path.GetTempPath(), "ap-home-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var scratch = ScriptScratch.Create(home, Guid.NewGuid(), ScriptInterpreters.Sh, "true\n");
            Assert.Contains(Path.Combine(home, ScriptScratch.RunsFolder), scratch.Folder, StringComparison.Ordinal);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(scratch.Folder) & (UnixFileMode)0b111_111_111);
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void The_sweep_of_the_work_folder_removes_only_folders_named_like_a_run_and_never_a_running_one()
    {
        var work = Path.Combine(Path.GetTempPath(), "ap-work-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stale = Guid.NewGuid();
            var live = Guid.NewGuid();
            Directory.CreateDirectory(Path.Combine(work, stale.ToString("N")));
            Directory.CreateDirectory(Path.Combine(work, live.ToString("N")));
            Directory.CreateDirectory(Path.Combine(work, "something-else"));
            Assert.Equal(1, ScriptScratch.SweepFolder(work, id => id == live, onlyRunFolders: true));
            Assert.False(Directory.Exists(Path.Combine(work, stale.ToString("N"))));
            Assert.True(Directory.Exists(Path.Combine(work, live.ToString("N"))));
            Assert.True(Directory.Exists(Path.Combine(work, "something-else")));
            Assert.Equal(0, ScriptScratch.SweepFolder(Path.Combine(work, "nope"), _ => false, true));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // ---- the policy file --------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_policy_file_keeps_the_run_account_across_a_reinstall_and_drops_a_name_that_is_not_one()
    {
        var path = Path.Combine(Path.GetTempPath(), "ap-policy-" + Guid.NewGuid().ToString("N"), "exec.json");
        try
        {
            ExecPolicyLoader.Save(path, new ExecPolicy(ExecLevels.Argv, [], [], RunAccount: "ap-run"));
            Assert.Equal("ap-run", ExecPolicyLoader.ReadExisting(path)!.RunAccount);
            Assert.Contains("runs as ap-run", ExecPolicyLoader.Describe(ExecPolicyLoader.ReadExisting(path)!), StringComparison.Ordinal);

            File.WriteAllText(path, """{"level":"argv","allowedExecutables":[],"allowedRoots":[],"runAccount":"root"}""");
            Assert.Null(ExecPolicyLoader.ReadExisting(path)!.RunAccount);

            ExecPolicyLoader.Save(path, new ExecPolicy(ExecLevels.Argv, [], []));
            Assert.Null(ExecPolicyLoader.ReadExisting(path)!.RunAccount);
            Assert.DoesNotContain("runAccount", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void A_service_policy_that_names_a_bad_run_account_is_not_believed_at_all()
    {
        var path = Path.Combine(Path.GetTempPath(), "ap-policy-" + Guid.NewGuid().ToString("N"), "exec.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """{"level":"shell","allowedExecutables":[],"allowedRoots":[],"runAccount":"root"}""");
            // A temp folder is not root-owned, so the file is distrusted anyway: off, which is what a doubtful file means.
            Assert.Equal(ExecLevels.Off, ExecPolicyLoader.ReadServiceFile(path).Level);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    // ---- the installer ---------------------------------------------------------------------------------------------------------

    private static ServiceInstaller With(ServiceInstallerFixture fx) =>
        new(fx.Output, fx.Run, fx.Os, true, fx.Layout, fx.Definition, fx.RunDir, (_, _) => fx.DsclList) { SudoersFolder = $"{fx.Dir}/sudoers.d" };

    [Fact]
    public void Linux_creates_the_account_adds_the_service_to_its_group_makes_the_shared_folder_and_writes_a_checked_sudoers_file()
    {
        using var fx = new ServiceInstallerFixture(OsKinds.Linux);
        fx.Answer = line => line.StartsWith("/usr/bin/id -u", StringComparison.Ordinal) ? 1 : 0; // neither account exists yet

        Assert.Equal(0, With(fx).Install(fx.Current, ExecLevels.Shell, allowRoot: false, runAccount: "ap-run"));

        var work = $"{fx.Layout.Home}/run-work";
        Assert.True(fx.Called("/usr/sbin/useradd --system --user-group --home-dir " + work));
        Assert.True(fx.Called("/usr/sbin/usermod -aG ap-run aipod"));
        Assert.True(fx.Called($"/bin/chown aipod:ap-run {work}"));
        Assert.True(fx.Called("/usr/sbin/visudo -cf"));
        Assert.True(Directory.Exists(work));
        // The setgid bit is asked for, but the system keeps it only for a folder whose group is one its owner belongs to, which root's chown makes true on a real
        // install and a fake chown does not: the group access is what can be checked here, the setgid bit is in the host checks (ADR-0010 §8).
        Assert.Equal(UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute, File.GetUnixFileMode(work) & UnixFileMode.GroupRead | File.GetUnixFileMode(work) & UnixFileMode.GroupWrite | File.GetUnixFileMode(work) & UnixFileMode.GroupExecute);
        Assert.Equal(RunAccount.SudoersText("aipod", "ap-run"), File.ReadAllText($"{fx.Dir}/sudoers.d/aipod-run"));
        Assert.Equal("ap-run", ExecPolicyLoader.ReadExisting(fx.Layout.ExecConfigPath)!.RunAccount);
        var order = fx.Calls.ToList();
        Assert.True(order.FindIndex(c => c.Contains("visudo", StringComparison.Ordinal)) > order.FindIndex(c => c.Contains("usermod", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_account_that_already_exists_is_not_created_again()
    {
        using var fx = new ServiceInstallerFixture(OsKinds.Linux);
        Assert.Equal(0, With(fx).Install(fx.Current, ExecLevels.Argv, allowRoot: false, runAccount: "ap-run")); // the fake answers success to "id -u": both exist
        Assert.False(fx.Called("/usr/sbin/useradd"));
        Assert.True(fx.Called("/usr/sbin/usermod -aG ap-run aipod"));
    }

    [Fact]
    public void macOS_creates_a_hidden_account_with_dscl_and_uses_dseditgroup_for_the_membership()
    {
        using var fx = new ServiceInstallerFixture(OsKinds.MacOs);
        fx.Answer = line => line.StartsWith("/usr/bin/dscl . -read /Users/_", StringComparison.Ordinal) || line.Contains("-read /Users/", StringComparison.Ordinal) ? 1 : 0;

        Assert.Equal(0, With(fx).Install(fx.Current, ExecLevels.Argv, allowRoot: false, runAccount: "_aprun"));

        Assert.True(fx.Called("/usr/bin/dscl . -create /Users/_aprun"));
        Assert.True(fx.Called("/usr/sbin/dseditgroup -o edit -a _aipod -t user _aprun"));
        Assert.True(fx.Called($"/usr/sbin/chown _aipod:_aprun {fx.Layout.Home}/run-work"));
        Assert.True(File.Exists($"{fx.Dir}/sudoers.d/aipod-run"));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("aipod")]
    [InlineData("a b")]
    public void A_run_account_that_is_privileged_the_service_itself_or_not_a_name_stops_the_install_and_leaves_no_sudoers_file(string account)
    {
        using var fx = new ServiceInstallerFixture(OsKinds.Linux);
        Assert.Equal(1, With(fx).Install(fx.Current, ExecLevels.Argv, allowRoot: false, runAccount: account));
        Assert.Contains("cannot be the run account", fx.Text, StringComparison.Ordinal);
        Assert.False(File.Exists($"{fx.Dir}/sudoers.d/aipod-run"));
        Assert.False(fx.Called("/usr/sbin/visudo"));
    }

    [Fact]
    public void A_sudoers_file_visudo_does_not_accept_never_reaches_sudo_and_the_install_is_undone()
    {
        using var fx = new ServiceInstallerFixture(OsKinds.Linux);
        fx.Answer = line => line.StartsWith("/usr/sbin/visudo", StringComparison.Ordinal) ? 1 : 0;
        Assert.Equal(1, With(fx).Install(fx.Current, ExecLevels.Argv, allowRoot: false, runAccount: "ap-run"));
        Assert.False(File.Exists($"{fx.Dir}/sudoers.d/aipod-run"));
        Assert.Empty(Directory.GetFiles($"{fx.Dir}/sudoers.d")); // the temporary file is gone too
        Assert.Contains("Undone", fx.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_refuses_a_run_account_before_anything_is_touched()
    {
        using var fx = new ServiceInstallerFixture(OsKinds.Windows);
        Assert.Equal(2, With(fx).Install(fx.Current, ExecLevels.Argv, allowRoot: false, runAccount: "x"));
        Assert.Contains("not supported on Windows", fx.Text, StringComparison.Ordinal);
        Assert.Empty(fx.Calls);
    }

    [Fact]
    public void Uninstalling_the_service_removes_the_sudoers_drop_in()
    {
        using var fx = new ServiceInstallerFixture(OsKinds.Linux);
        Assert.Equal(0, With(fx).Install(fx.Current, ExecLevels.Argv, allowRoot: false, runAccount: "ap-run"));
        Assert.True(File.Exists($"{fx.Dir}/sudoers.d/aipod-run"));
        Assert.Equal(0, With(fx).Uninstall());
        Assert.False(File.Exists($"{fx.Dir}/sudoers.d/aipod-run"));
        Assert.Contains("Removed the sudoers drop-in", fx.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reinstall_without_the_option_keeps_the_account_an_admin_chose()
    {
        using var fx = new ServiceInstallerFixture(OsKinds.Linux);
        Assert.Equal(0, With(fx).Install(fx.Current, ExecLevels.Argv, allowRoot: false, runAccount: "ap-run"));
        Assert.Equal(0, With(fx).Install(fx.Current, ExecLevels.Argv, allowRoot: false));
        Assert.Equal("ap-run", ExecPolicyLoader.ReadExisting(fx.Layout.ExecConfigPath)!.RunAccount);
    }

    [Fact]
    public void The_install_command_parses_the_option_with_and_without_a_name_and_refuses_a_bad_one()
    {
        var calls = new List<string>();
        int Run(string tool, IReadOnlyList<string> args)
        {
            calls.Add($"{tool} {string.Join(' ', args)}");
            return 0;
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Assert.Equal(2, ServiceCommands.Install(["install", "--service", "--run-account", "root"], stdout, stderr, Run, isAdmin: false));
        Assert.Contains("plain account name", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(calls);
    }
}
