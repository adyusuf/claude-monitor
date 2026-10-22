using Podium.Agent.Auth;
using Podium.Agent.Install;

namespace Podium.Agent.Tests;

/// <summary>The command line of "install --standalone" (ADR-0008 §3g): what is refused before anything is touched. The paths of a real autostart are never used here.</summary>
public sealed class StandaloneCommandsTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly StringWriter stdout = new();
    private readonly StringWriter stderr = new();
    private readonly List<string> tools = [];

    public void Dispose() => home.Dispose();

    private Task<int> InstallAsync(bool root, Func<string?, Task<int>>? login = null, params string[] args) =>
        StandaloneCommands.InstallAsync(args, home.Config, stdout, stderr, TimeProvider.System, (tool, _) =>
        {
            tools.Add(tool);
            return 0;
        }, root, login);

    [Fact]
    public void The_flag_is_recognised_anywhere_in_the_arguments_and_not_confused_with_the_service_flag()
    {
        Assert.True(StandaloneCommands.IsStandalone(["install", "--standalone"]));
        Assert.True(StandaloneCommands.IsStandalone(["install", "--server", "https://x.example.test", "--standalone"]));
        Assert.False(StandaloneCommands.IsStandalone(["install", "--service"]));
        Assert.False(StandaloneCommands.IsStandalone([]));
    }

    [Theory]
    [InlineData("http://podium.example.test")]
    [InlineData("ftp://podium.example.test")]
    [InlineData("not a url")]
    [InlineData("https://podium.example.test/a b")]
    [InlineData("https://podium.example.test/%00")]
    public async Task A_server_that_is_not_an_https_address_is_refused_and_nothing_is_touched(string server)
    {
        var code = await InstallAsync(false, null, "install", "--standalone", "--server", server);
        Assert.Equal(2, code);
        Assert.Contains("takes the https address", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(tools);
        Assert.False(File.Exists(home.Config.IdentityPath));
    }

    [Fact]
    public async Task Plain_http_is_allowed_only_for_the_local_machine()
    {
        var code = await InstallAsync(true, null, "install", "--standalone", "--server", "http://localhost:5080");
        Assert.Equal(2, code); // past the address check: stopped by the root refusal, not by the address
        Assert.Contains("not as root", stdout.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("takes the https address", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_exec_level_is_refused_before_the_installer_starts()
    {
        var code = await InstallAsync(false, null, "install", "--standalone", "--exec", "everything");
        Assert.Equal(2, code);
        Assert.Contains("--exec takes", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(tools);
    }

    [Fact]
    public async Task A_root_account_is_refused_by_the_command_with_nothing_written_and_no_tool_run()
    {
        var code = await InstallAsync(true, null, "install", "--standalone", "--server", "https://podium.example.test");
        Assert.Equal(2, code);
        Assert.Contains("not as root", stdout.ToString(), StringComparison.Ordinal);
        Assert.Empty(tools);
        Assert.False(File.Exists(home.Config.IdentityPath));
    }

    [Fact]
    public async Task Without_a_connection_or_a_server_it_asks_for_the_server_and_runs_no_login()
    {
        var logins = 0;
        var code = await InstallAsync(false, _ =>
        {
            logins++;
            return Task.FromResult(0);
        }, "install", "--standalone");

        Assert.Equal(2, code);
        Assert.Contains("--server", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, logins);
        Assert.False(Directory.Exists(Path.Combine(home.Config.Home, "bin"))); // nothing was copied
    }

    [Fact]
    public async Task A_valid_exec_level_is_saved_in_the_identity_like_the_interactive_install_does_even_when_the_install_then_stops()
    {
        var code = await InstallAsync(false, null, "install", "--standalone", "--exec", "argv");
        Assert.Equal(2, code); // not connected, no server
        Assert.Equal("argv", Identity.Peek(home.Config)!.ExecLevel);
        Assert.Contains("Remote runs on this machine: argv", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_server_of_the_environment_or_the_identity_is_used_when_none_is_given()
    {
        var saved = Identity.Load(home.Config) with { Server = "https://saved.example.test" };
        saved.Save(home.Config);
        string? asked = null;

        var code = await InstallAsync(false, server =>
        {
            asked = server;
            return Task.FromResult(1); // the login does not complete: the install stops before touching anything
        }, "install", "--standalone");

        Assert.Equal(1, code);
        Assert.Equal("https://saved.example.test", asked);
        Assert.False(Directory.Exists(Path.Combine(home.Config.Home, "bin"))); // the login came first: nothing was copied
    }
}
