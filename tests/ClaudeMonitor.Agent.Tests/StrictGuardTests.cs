using System.Runtime.Versioning;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Decision §9.13 on the target: a machine that claims to be production or a database refuses a shell command or an interpreter run the API did not mark as approved with a two-step code.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class StrictGuardTests : IDisposable
{
    private readonly ExecFixture fx = new();

    public void Dispose() => fx.Dispose();

    private static ExecPolicy Claimed(string? environment, string? function = null, string level = ExecLevels.Shell) =>
        new(level, [], [], environment, function);

    private static readonly string[] Interpreter = ["/bin/sh", "-c", "true"];

    [Theory]
    [InlineData("production", "application")]
    [InlineData("test", "database")]
    public void A_strict_claim_refuses_a_shell_command_not_approved_with_a_code_and_allows_it_with_one(string environment, string function)
    {
        if (!ExecFixture.Unix) return;
        var run = ExecFixture.Shell("echo hi");
        Assert.Equal(RemoteErrors.TotpRequired, fx.Check(run, Claimed(environment, function)).Error);
        Assert.True(fx.Check(run with { ApprovedWithTotp = true }, Claimed(environment, function)).Allowed);
    }

    [Fact]
    public void A_strict_claim_refuses_an_interpreter_run_not_approved_with_a_code_and_does_not_ask_again_when_it_was()
    {
        if (!ExecFixture.Unix) return;
        var run = ExecFixture.Argv(Interpreter);
        Assert.Equal(RemoteErrors.TotpRequired, fx.Check(run, Claimed("production")).Error);
        Assert.NotEqual(RemoteErrors.TotpRequired, fx.Check(run with { ApprovedWithTotp = true }, Claimed("production")).Error);
    }

    [Fact]
    public void A_plain_argv_run_needs_no_code_on_a_strict_machine()
    {
        if (!ExecFixture.Unix) return;
        var run = ExecFixture.Argv([ExecFixture.TrustedExe]);
        Assert.True(fx.Check(run, Claimed("production", "database", ExecLevels.Argv)).Allowed);
    }

    [Fact]
    public void A_machine_that_claims_nothing_strict_keeps_asking_for_no_code()
    {
        if (!ExecFixture.Unix) return;
        Assert.True(fx.Check(ExecFixture.Shell("echo hi"), Claimed("test", "application")).Allowed);
        Assert.True(fx.Check(ExecFixture.Shell("echo hi"), Claimed(null)).Allowed);
        Assert.NotEqual(RemoteErrors.TotpRequired, fx.Check(ExecFixture.Argv(Interpreter), Claimed("staging")).Error);
    }

    [Fact]
    public void The_exec_level_is_still_checked_first()
    {
        if (!ExecFixture.Unix) return;
        Assert.Equal(ExecErrors.ExecLevel, fx.Check(ExecFixture.Shell("echo hi"), Claimed("production", level: ExecLevels.Argv)).Error);
    }
}
