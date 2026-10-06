using System.Runtime.Versioning;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class ExecGuardTests : IDisposable
{
    private readonly ExecFixture fx = new();

    public void Dispose() => fx.Dispose();

    private static readonly ExecPolicy Argv = ExecFixture.Level(ExecLevels.Argv);

    [Fact]
    public void Level_off_and_an_unknown_level_refuse_every_run()
    {
        if (!ExecFixture.Unix) return;
        var run = ExecFixture.Argv([ExecFixture.TrustedExe]);
        Assert.Equal(ExecErrors.ExecLevel, fx.Check(run, ExecPolicy.Off).Error);
        Assert.Equal(ExecErrors.ExecLevel, fx.Check(run, ExecFixture.Level("root")).Error);
        Assert.Equal(ExecErrors.ExecLevel, fx.Check(ExecFixture.Shell("true"), ExecPolicy.Off).Error);
        Assert.True(fx.Check(run, Argv).Allowed);
    }

    [Fact]
    public void Exec_level_argv_refuses_a_shell_run_and_level_shell_allows_it_in_the_service_home()
    {
        if (!ExecFixture.Unix) return;
        Assert.Equal(ExecErrors.ExecLevel, fx.Check(ExecFixture.Shell("echo hi"), Argv).Error);

        var decision = fx.Check(ExecFixture.Shell("echo hi"), ExecFixture.Level(ExecLevels.Shell));
        Assert.True(decision.Allowed);
        Assert.Equal("/bin/sh", decision.ResolvedExe);
    }

    [Fact]
    public void A_shell_run_never_carries_a_grant_a_directory_or_an_empty_command()
    {
        if (!ExecFixture.Unix) return;
        var shell = ExecFixture.Level(ExecLevels.Shell);
        var grant = new GrantTemplate([ExecFixture.TrustedExe], fx.Root, 60);
        Assert.Equal(ExecErrors.GrantMismatch, fx.Check(ExecFixture.Shell("true", grant: grant), shell).Error);
        Assert.Equal(ExecErrors.GrantMismatch, fx.Check(ExecFixture.Shell("true", grantId: Guid.NewGuid()), shell).Error);
        Assert.Equal(ExecErrors.ShellCwd, fx.Check(ExecFixture.Shell("true", cwd: fx.Root), shell).Error);
        Assert.Equal(ExecErrors.Malformed, fx.Check(ExecFixture.Shell(""), shell).Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    [InlineData(-5)]
    public void A_timeout_outside_one_second_to_an_hour_is_refused(int seconds)
    {
        if (!ExecFixture.Unix) return;
        Assert.Equal(ExecErrors.Timeout, fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe], timeout: seconds), Argv).Error);
        Assert.True(fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe], timeout: 3600), Argv).Allowed);
        Assert.True(fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe], timeout: 1), Argv).Allowed);
    }

    [Fact]
    public void An_unknown_os_is_refused_whatever_the_run()
    {
        if (!ExecFixture.Unix) return;
        Assert.Equal(GrantErrors.UnknownOs, ExecGuard.Check(ExecFixture.Argv([ExecFixture.TrustedExe]), Argv, "plan9", fx.Home).Error);
    }

    [Fact]
    public void A_per_call_argv_run_needs_an_absolute_program_that_exists()
    {
        if (!ExecFixture.Unix) return;
        Assert.Equal(ExecErrors.Malformed, fx.Check(ExecFixture.Argv([]), Argv).Error);
        Assert.Equal(ExecErrors.ExeNotAbsolute, fx.Check(ExecFixture.Argv(["true"]), Argv).Error);
        Assert.Equal(ExecErrors.ExeNotAbsolute, fx.Check(ExecFixture.Argv(["../usr/bin/true"]), Argv).Error);
        Assert.Equal(ExecErrors.ExeUnresolved, fx.Check(ExecFixture.Argv([Path.Combine(fx.Dir, "nothing")]), Argv).Error);
        Assert.Equal(ExecErrors.ExeUnresolved, fx.Check(ExecFixture.Argv(["/usr/bin"]), Argv).Error); // a folder is no program

        var ok = fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe, "--anything"]), Argv);
        Assert.True(ok.Allowed);
        Assert.Equal(ExecPaths.RealPath(ExecFixture.TrustedExe, ExecFixture.Os, false), ok.ResolvedExe);
    }

    [Fact]
    public void A_program_the_current_user_owns_is_refused_as_untrusted_even_when_it_is_executable()
    {
        if (!ExecFixture.Unix) return;
        var mine = ExecFixture.File(fx.Outside, "mine", "#!/bin/sh\n");
        File.SetUnixFileMode(mine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Equal(ExecErrors.ExeUntrusted, fx.Check(ExecFixture.Argv([mine]), Argv).Error);
    }

    [Fact]
    public void A_program_file_that_others_can_write_is_not_trusted_and_a_system_program_is()
    {
        if (!ExecFixture.Unix) return;
        var file = ExecFixture.File(fx.Outside, "tool");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
        Assert.False(ExecPaths.IsTrustedExecutable(file, ExecFixture.Os));
        Assert.True(ExecPaths.IsTrustedExecutable(ExecFixture.TrustedExe, ExecFixture.Os));
    }

    [Fact]
    public void A_grant_that_does_not_match_the_run_is_refused_and_so_is_a_grant_id_without_a_template()
    {
        if (!ExecFixture.Unix) return;
        var grant = new GrantTemplate([ExecFixture.TrustedExe, "{int:1..5}"], fx.Root, 60);
        Assert.True(fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe, "3"], grant, fx.Root), Argv).Allowed);
        Assert.Equal(ExecErrors.GrantMismatch, fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe, "9"], grant, fx.Root), Argv).Error);
        Assert.Equal(ExecErrors.GrantMismatch, fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe], grant, fx.Root), Argv).Error);
        Assert.Equal(ExecErrors.GrantMismatch, fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe, "3"], grant, fx.Outside), Argv).Error);
        Assert.Equal(ExecErrors.GrantMismatch, fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe, "3"], grant, fx.Root, timeout: 61), Argv).Error);
        Assert.Equal(ExecErrors.GrantMismatch, fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe], grantId: Guid.NewGuid()), Argv).Error);
    }

    [Fact]
    public void A_grant_template_that_is_itself_invalid_is_refused_on_the_target_too()
    {
        if (!ExecFixture.Unix) return;
        var shellGrant = new GrantTemplate(["/bin/sh", "-c"], fx.Root, 60);
        Assert.Equal(ExecErrors.GrantMismatch, fx.Check(ExecFixture.Argv(["/bin/sh", "-c"], shellGrant, fx.Root), Argv).Error);
    }

    [Fact]
    public void A_standing_grant_never_covers_a_program_that_is_an_interpreter_behind_a_link_but_a_per_call_run_may()
    {
        if (!ExecFixture.Unix) return;
        var link = Path.Combine(fx.Outside, "friendly");
        File.CreateSymbolicLink(link, "/bin/sh");
        var grant = new GrantTemplate([link], fx.Root, 60);
        Assert.Equal(ExecErrors.ExeNeverGrantable, fx.Check(ExecFixture.Argv([link], grant, fx.Root), Argv).Error);

        var perCall = fx.Check(ExecFixture.Argv(["/bin/sh", "-c", "true"]), Argv);
        Assert.True(perCall.Allowed);
    }

    [Fact]
    public void The_executable_ceiling_lets_only_the_listed_real_programs_run()
    {
        if (!ExecFixture.Unix) return;
        var real = ExecPaths.RealPath(ExecFixture.TrustedExe, ExecFixture.Os, false)!;
        var other = ExecPaths.RealPath("/bin/cat", ExecFixture.Os, false)!;
        Assert.True(fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe]), ExecFixture.Level(ExecLevels.Argv, executables: [real])).Allowed);
        Assert.Equal(ExecErrors.ExeNotAllowed, fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe]), ExecFixture.Level(ExecLevels.Argv, executables: [other])).Error);
        Assert.Equal(ExecErrors.ExeNotAllowed, fx.Check(ExecFixture.Argv([ExecFixture.TrustedExe]), ExecFixture.Level(ExecLevels.Argv, executables: [Path.Combine(fx.Dir, "missing")])).Error);
        // A link to an allowed program is judged by what it points to.
        var link = Path.Combine(fx.Outside, "t");
        File.CreateSymbolicLink(link, ExecFixture.TrustedExe);
        Assert.Equal(ExecErrors.ExeNotAllowed, fx.Check(ExecFixture.Argv([link]), ExecFixture.Level(ExecLevels.Argv, executables: [other])).Error);
        Assert.True(fx.Check(ExecFixture.Argv([link]), ExecFixture.Level(ExecLevels.Argv, executables: [real])).Allowed);
    }
}
