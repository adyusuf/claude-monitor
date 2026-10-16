using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The target's own check of a script run before anything is written or started (ADR-0008 §3c): it repeats what the API checked.</summary>
public sealed class ScriptGuardTests
{
    private const string Sh = "/bin/sh";

    private static RunMessage Script(string interpreter = ScriptInterpreters.Sh, string? body = "echo hi\n", IReadOnlyList<string>? args = null, bool totp = false, int timeout = 30,
        string? cwd = null, GrantTemplate? grant = null, Guid? grantId = null, IReadOnlyList<string>? argv = null, string? shell = null) =>
        new(Guid.NewGuid(), RunModes.Script, argv, shell, cwd, timeout, DateTimeOffset.UtcNow.AddMinutes(1), grantId, grant, null, interpreter, body, args, 8 * 1024 * 1024, totp);

    private static ExecDecision Check(RunMessage run, ExecPolicy? policy = null, Func<string, string?>? toolPath = null, Func<string, bool>? trusted = null) =>
        ExecGuard.Check(run, policy ?? new ExecPolicy(ExecLevels.Shell, [], []), ExecFixture.Os, "/home", toolPath ?? (name => name == ToolNames.Sh ? Sh : null), trusted ?? (_ => true));

    [Fact]
    public void A_script_at_the_shell_level_is_allowed_and_the_trusted_interpreter_is_the_executable()
    {
        var decision = Check(Script());
        Assert.Equal((true, null, Sh), (decision.Allowed, decision.Error, decision.ResolvedExe));
    }

    [Theory]
    [InlineData(ExecLevels.Off)]
    [InlineData(ExecLevels.Argv)]
    public void A_script_needs_the_shell_level(string level) =>
        Assert.Equal(ExecErrors.ExecLevel, Check(Script(), new ExecPolicy(level, [], [])).Error);

    [Fact]
    public void A_policy_with_a_ceiling_never_allows_a_script_as_it_never_allows_shell()
    {
        Assert.Equal(ExecErrors.ExecLevel, Check(Script(), new ExecPolicy(ExecLevels.Shell, ["/usr/bin/true"], [])).Error);
        Assert.Equal(ExecErrors.ExecLevel, Check(Script(), new ExecPolicy(ExecLevels.Shell, [], ["/srv"])).Error);
    }

    [Fact]
    public void A_script_never_comes_with_a_grant_argv_shell_text_or_a_working_directory()
    {
        Assert.Equal(ExecErrors.GrantMismatch, Check(Script(grantId: Guid.NewGuid())).Error);
        Assert.Equal(ExecErrors.GrantMismatch, Check(Script(grant: new GrantTemplate(["/bin/ls"], "/", 30))).Error);
        Assert.Equal(ExecErrors.Malformed, Check(Script(argv: ["/bin/ls"])).Error);
        Assert.Equal(ExecErrors.Malformed, Check(Script(shell: "ls")).Error);
        Assert.Equal(ExecErrors.ShellCwd, Check(Script(cwd: "/tmp")).Error);
    }

    [Theory]
    [InlineData("perl", "echo")]
    [InlineData("sh", null)]
    [InlineData("sh", "")]
    [InlineData("sh", "echo \0")]
    public void An_unknown_interpreter_or_a_missing_empty_or_NUL_body_is_malformed(string interpreter, string? body) =>
        Assert.Equal(ExecErrors.Malformed, Check(Script(interpreter, body)).Error);

    [Fact]
    public void The_body_and_the_arguments_are_limited_the_same_way_the_api_limits_them()
    {
        Assert.True(Check(Script(body: new string('a', ScriptInterpreters.BodyCeiling))).Allowed);
        Assert.Equal(ExecErrors.Malformed, Check(Script(body: new string('a', ScriptInterpreters.BodyCeiling + 1))).Error);
        Assert.Equal(ExecErrors.Malformed, Check(Script(args: [.. Enumerable.Repeat("a", ScriptInterpreters.ArgsMax + 1)])).Error);
        Assert.Equal(ExecErrors.Malformed, Check(Script(args: [new string('a', ScriptInterpreters.ArgChars + 1)])).Error);
        Assert.Equal(ExecErrors.Malformed, Check(Script(args: ["a\0b"])).Error);
    }

    [Fact]
    public void The_timeout_is_checked_like_any_run()
    {
        Assert.Equal(ExecErrors.Timeout, Check(Script(timeout: 0)).Error);
        Assert.Equal(ExecErrors.Timeout, Check(Script(timeout: 99_999)).Error);
    }

    [Fact]
    public void A_target_that_claims_to_be_strict_refuses_a_script_that_was_not_approved_with_a_code()
    {
        var strict = new ExecPolicy(ExecLevels.Shell, [], [], MachineEnvironments.Production, MachineFunctions.Application);
        Assert.Equal(RemoteErrors.TotpRequired, Check(Script(totp: false), strict).Error);
        Assert.True(Check(Script(totp: true), strict).Allowed);
        var database = new ExecPolicy(ExecLevels.Shell, [], [], MachineEnvironments.Test, MachineFunctions.Database);
        Assert.Equal(RemoteErrors.TotpRequired, Check(Script(totp: false), database).Error);
        var test = new ExecPolicy(ExecLevels.Shell, [], [], MachineEnvironments.Test, MachineFunctions.Application);
        Assert.True(Check(Script(totp: false), test).Allowed);
    }

    [Fact]
    public void A_missing_or_untrusted_interpreter_refuses_the_script_and_nothing_is_started_from_an_untrusted_file()
    {
        Assert.Equal(RemoteErrors.InterpreterMissing, Check(Script(), toolPath: _ => null).Error);
        Assert.Equal(RemoteErrors.InterpreterMissing, Check(Script(), trusted: _ => false).Error);
    }

    [Fact]
    public void Powershell_takes_pwsh_first_and_then_the_windows_powershell_and_the_first_trusted_one_wins()
    {
        var asked = new List<string>();
        var decision = Check(Script(ScriptInterpreters.PowerShell, "Get-Date"), toolPath: name =>
        {
            asked.Add(name);
            return name == ToolNames.WindowsPowerShell ? "/opt/ps" : "/opt/pwsh";
        }, trusted: real => real == "/opt/ps");
        Assert.Equal("/opt/ps", decision.ResolvedExe);
        Assert.Equal([ToolNames.PowerShell, ToolNames.WindowsPowerShell], asked);
    }
}
