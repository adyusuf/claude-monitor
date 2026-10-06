using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>When the Claude updater does NOTHING: no consent on both sides, no updatable install, a session that is not idle.</summary>
public sealed class ClaudeUpdaterGateTests
{
    private static AgentConfig FromEnvironment(AgentConfig c, string? flag) => AgentConfig.FromEnvironment(key => key switch
    {
        "CM_AGENT_HOME" => c.Home,
        "CM_CREDENTIALS" => "file",
        "CM_CLAUDE_UPDATE" => flag,
        "CM_CLAUDE_BINARY" => c.ClaudeBinary,
        "CLAUDE_CONFIG_DIR" => c.ClaudeConfigDir,
        "PATH" => "",
        _ => null,
    });

    // ---- consent ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task By_default_it_is_disabled_and_runs_nothing_and_leaves_no_trace()
    {
        using var kit = new ClaudeKit();
        kit.Idle();
        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Disabled, outcome.Code);
        kit.AssertNothingHappened();
        Assert.False(File.Exists(kit.Config.ClaudeUpdateStatePath), "no state file");
        Assert.False(File.Exists(kit.Config.ClaudeUpdateLockPath), "no lock file");
        Assert.False(File.Exists(kit.Config.ClaudeCancelPath));
        Assert.Equal("", kit.LogText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("True")]
    [InlineData("on")]
    public async Task This_machine_on_but_the_workspace_not_exactly_true_is_disabled(string? workspace)
    {
        using var kit = new ClaudeKit();
        kit.Idle();
        kit.Consent(machine: true, workspace);
        Assert.Equal(ClaudeCodes.Disabled, (await kit.RunAsync()).Code);
        kit.AssertNothingHappened();
        Assert.False(File.Exists(kit.Config.ClaudeUpdateStatePath));
    }

    [Fact]
    public async Task The_workspace_on_but_this_machine_off_is_disabled()
    {
        using var kit = new ClaudeKit();
        kit.Idle();
        kit.Consent(machine: false, workspace: "true");
        Assert.Equal(ClaudeCodes.Disabled, (await kit.RunAsync()).Code);
        kit.AssertNothingHappened();
        Assert.False(File.Exists(kit.Config.ClaudeUpdateStatePath));

        kit.Consent(machine: true, workspace: null);
        Assert.NotEqual(ClaudeCodes.Disabled, (await kit.RunAsync()).Code);
    }

    [Fact]
    public async Task The_saved_machine_setting_is_read_afresh_not_taken_from_the_configuration_the_daemon_started_with()
    {
        using var kit = new ClaudeKit(c => c with { ClaudeUpdateEnabled = true }); // a daemon that started when it was on
        kit.Idle();
        kit.Consent(machine: false, workspace: "true");
        Assert.Equal(ClaudeCodes.Disabled, (await kit.RunAsync()).Code);
        kit.AssertNothingHappened();
    }

    [Fact]
    public async Task CM_CLAUDE_UPDATE_on_wins_over_a_saved_off()
    {
        using var kit = new ClaudeKit(c => FromEnvironment(c, "on"), installed: false);
        kit.Consent(machine: false, workspace: "true");
        Assert.True(kit.Config.ClaudeUpdateFromEnvironment);
        Assert.Equal(ClaudeCodes.NoClaude, (await kit.RunAsync()).Code); // past the consent check
    }

    [Fact]
    public async Task CM_CLAUDE_UPDATE_off_wins_over_a_saved_on()
    {
        using var kit = new ClaudeKit(c => FromEnvironment(c, "off"), installed: false);
        kit.Consent(machine: true, workspace: "true");
        Assert.Equal(ClaudeCodes.Disabled, (await kit.RunAsync()).Code);
        Assert.False(File.Exists(kit.Config.ClaudeUpdateStatePath));
    }

    // ---- the install -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_desktop_app_install_is_never_touched_not_even_to_ask_its_version()
    {
        using var kit = new ClaudeKit(layout: ClaudeKit.Desktop);
        kit.AllowAll();
        kit.Idle();
        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Unsupported, outcome.Code);
        Assert.Contains("does not update", outcome.Detail, StringComparison.Ordinal);
        kit.AssertNothingHappened(ClaudeCodes.Unsupported);
        kit.AssertNextAt(kit.Config.ClaudeUpdateEvery);
        Assert.Contains("claude update unsupported-install", kit.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unrecognised_install_is_unsupported_too()
    {
        using var kit = new ClaudeKit(layout: "usr/local/bin/claude");
        kit.AllowAll();
        kit.Idle();
        Assert.Equal(ClaudeCodes.Unsupported, (await kit.RunAsync()).Code);
        kit.AssertNothingHappened(ClaudeCodes.Unsupported);
    }

    [Fact]
    public async Task No_claude_means_no_claude_and_nothing_is_run_or_announced()
    {
        using var kit = new ClaudeKit(installed: false);
        kit.AllowAll();
        kit.Idle();
        Assert.Equal(ClaudeCodes.NoClaude, (await kit.RunAsync()).Code);
        kit.AssertNothingHappened(ClaudeCodes.NoClaude);
        kit.AssertNextAt(kit.Config.ClaudeUpdateEvery);
    }

    // ---- sessions that are not idle ----------------------------------------------------------------------------

    [Theory]
    [InlineData("busy", "a session is busy")]
    [InlineData("waiting", "a session is waiting")]
    [InlineData("thinking", "does not know")]
    public async Task A_session_that_is_not_idle_means_waiting_with_the_reason_and_no_countdown_and_no_notice(string status, string reason)
    {
        using var kit = new ClaudeKit();
        kit.AllowAll();
        kit.Files.Session(100, status, kit.Now.AddHours(-1));
        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Waiting, outcome.Code);
        Assert.Contains(reason, outcome.Detail, StringComparison.Ordinal);
        kit.AssertNothingHappened(ClaudeCodes.Waiting);
        Assert.Null(kit.State.CountdownUntil);
        Assert.Null(kit.State.NextAt);
        Assert.Contains(reason, kit.State.Detail, StringComparison.Ordinal);
        Assert.Equal(0, kit.Clock.Ticks); // no countdown wait ever started
        Assert.DoesNotContain("will be updated", kit.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_idle_for_less_than_the_required_time_is_waiting()
    {
        using var kit = new ClaudeKit();
        kit.AllowAll();
        kit.Idle(since: TimeSpan.FromMinutes(2));
        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Waiting, outcome.Code);
        Assert.Contains("less than 10 min", outcome.Detail, StringComparison.Ordinal);
        kit.AssertNothingHappened(ClaudeCodes.Waiting);
        Assert.Equal(0, kit.Clock.Ticks);
    }

    [Fact]
    public async Task Sessions_that_cannot_be_read_mean_waiting()
    {
        using var kit = new ClaudeKit();
        kit.AllowAll();
        Assert.Equal(ClaudeCodes.Waiting, (await kit.RunAsync()).Code); // no sessions folder at all
        kit.Idle();
        kit.Files.Write("999.json", "{ broken");
        var outcome = await kit.RunAsync();
        Assert.Equal(ClaudeCodes.Waiting, outcome.Code);
        Assert.Contains("999.json could not be read", outcome.Detail, StringComparison.Ordinal);
        kit.AssertNothingHappened(ClaudeCodes.Waiting);
    }

    [Fact]
    public async Task The_same_reason_is_logged_once_across_polls_and_a_new_reason_is_logged_again()
    {
        using var kit = new ClaudeKit();
        kit.AllowAll();
        kit.Files.Session(100, "busy", kit.Now.AddHours(-1));
        await kit.RunAsync();
        await kit.RunAsync();
        await kit.RunAsync();
        Assert.Single(kit.LogText.Split('\n'), l => l.Contains("claude update waiting: a session is busy", StringComparison.Ordinal));

        kit.Files.Session(100, "waiting", kit.Now.AddHours(-1));
        await kit.RunAsync();
        await kit.RunAsync();
        var lines = kit.LogText.Split('\n').Where(l => l.Contains("claude update waiting:", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains("a session is waiting", lines[1], StringComparison.Ordinal);
        kit.AssertNothingHappened(ClaudeCodes.Waiting);
    }
}
