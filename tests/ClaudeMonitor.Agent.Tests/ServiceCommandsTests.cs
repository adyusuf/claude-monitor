using System.Runtime.Versioning;
using System.Text.Json;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Install;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

// Every call below passes isAdmin: false or fails validation first, so no service, account or system folder is ever touched.
[UnsupportedOSPlatform("windows")]
public sealed class ServiceCommandsTests : IDisposable
{
    private readonly StringWriter stdout = new();
    private readonly StringWriter stderr = new();
    private readonly List<string> ran = [];
    private readonly TempHome home = new();

    public void Dispose() => home.Dispose();

    private int Install(params string[] args) => ServiceCommands.Install(args, stdout, stderr, (tool, _) =>
    {
        ran.Add(tool);
        return 0;
    }, isAdmin: false);

    private static bool PolicyExists() => File.Exists(ServiceLayout.For(AgentConfig.Os).ExecConfigPath);

    [Theory]
    [InlineData("--exec", "root")]
    [InlineData("--exec", "ARGV")]
    [InlineData("--exec", "")]
    public void An_unknown_exec_level_is_refused_with_exit_2_and_the_list_of_levels(params string[] option)
    {
        Assert.Equal(2, Install(["--service", .. option]));
        Assert.Contains("--exec takes off, argv, shell", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(ran);
        Assert.DoesNotContain("sudo", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_exec_option_with_no_value_is_refused()
    {
        Assert.Equal(2, Install("--service", "--exec"));
        Assert.Contains("not \"\"", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://monitor.invalid")]
    [InlineData("monitor.invalid")]
    [InlineData("ftp://monitor.invalid")]
    [InlineData("https://monitor.invalid/a b")]
    [InlineData("https://monitor.invalid/%41")]
    [InlineData("https://monitor.invalid/a|b")]
    [InlineData("https://monitor.invalid/\"x")]
    public void A_server_that_is_not_a_plain_https_address_is_refused_with_exit_2(string server)
    {
        Assert.Equal(2, Install("--service", "--server", server));
        Assert.Contains("takes the https address of Claude Monitor", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(ran);
    }

    [Fact]
    public void A_valid_https_server_passes_validation_and_without_admin_rights_the_hint_follows()
    {
        Assert.Equal(2, Install("--service", "--server", "https://monitor.invalid/"));
        Assert.Equal("", stderr.ToString());
        Assert.Contains("sudo", stdout.ToString(), StringComparison.Ordinal);
        Assert.Empty(ran);
    }

    [Fact]
    public void The_exec_level_asked_for_is_the_one_the_hint_repeats_and_a_first_install_without_the_option_is_off()
    {
        if (PolicyExists()) return; // a real policy on this machine would decide the level; nothing is written or changed here
        Assert.Equal(2, Install("--service"));
        Assert.Contains("--exec off", stdout.ToString(), StringComparison.Ordinal);

        Assert.Equal(2, Install("--service", "--exec", "argv", "--allow-root"));
        Assert.Contains("--exec argv --allow-root", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Warning", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Level_shell_prints_a_warning_before_anything_else()
    {
        Install("--service", "--exec", "shell");
        Assert.Contains("Warning: exec level shell lets approved runs execute any shell text on this machine.", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstalling_without_admin_rights_gives_the_sudo_hint_and_runs_nothing()
    {
        Assert.Equal(2, ServiceCommands.Uninstall(stdout, (tool, _) =>
        {
            ran.Add(tool);
            return 0;
        }, isAdmin: false));
        Assert.Contains("uninstall --service", stdout.ToString(), StringComparison.Ordinal);
        Assert.Empty(ran);
    }

    [Fact]
    public void The_service_flag_is_recognised_wherever_it_stands()
    {
        Assert.True(ServiceCommands.IsService(["install", "--exec", "argv", "--service"]));
        Assert.False(ServiceCommands.IsService(["install", "--exec", "argv"]));
        Assert.False(ServiceCommands.IsService(null!));
    }

    // ---- an interactive agent ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("off")]
    [InlineData("argv")]
    [InlineData("shell")]
    public void The_exec_level_is_saved_in_agent_json_and_the_rest_of_the_identity_is_kept(string level)
    {
        var agent = Guid.NewGuid();
        new Identity(new string('k', 24), "https://monitor.invalid", agent, Guid.NewGuid(), StopWaitSeconds: 30, Push: true).Save(home.Config);

        Assert.Equal(0, ServiceCommands.SaveExecLevel(["install", "--exec", level], home.Config, stdout, stderr));

        var saved = Identity.Peek(home.Config)!;
        Assert.Equal((level, "https://monitor.invalid", agent, 30, true), (saved.ExecLevel, saved.Server, saved.AgentId, saved.StopWaitSeconds, saved.Push));
        Assert.Contains($"Remote runs on this machine: {level}", stdout.ToString(), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(File.ReadAllText(home.Config.IdentityPath));
        Assert.Equal(level, json.RootElement.GetProperty("execLevel").GetString());
    }

    [Fact]
    public void An_invalid_level_changes_nothing_and_a_missing_one_means_off()
    {
        new Identity(new string('k', 24), ExecLevel: ExecLevels.Argv).Save(home.Config);

        Assert.Equal(2, ServiceCommands.SaveExecLevel(["install", "--exec", "root"], home.Config, stdout, stderr));
        Assert.Equal(ExecLevels.Argv, Identity.Peek(home.Config)!.ExecLevel);
        Assert.Contains("--exec takes", stderr.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, ServiceCommands.SaveExecLevel(["install"], home.Config, stdout, stderr));
        Assert.Equal(ExecLevels.Off, Identity.Peek(home.Config)!.ExecLevel); // fail-closed default
    }
}
