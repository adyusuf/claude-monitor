using Podium.Agent.Config;
using Podium.Agent.Install;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>ADR-0013 step 1: which folder is the home when no variable says; a machine keeps the one it uses, a new one gets the aipod folder, nothing is moved.</summary>
public sealed class HomeResolverTests
{
    private static readonly string[] Systems = [OsKinds.MacOs, OsKinds.Windows, OsKinds.Linux];

    private static Func<string, bool> Files(params string[] present) => path => present.Contains(path);

    [Fact]
    public void The_aipod_folders_are_the_library_folder_on_macOS_and_a_dot_folder_elsewhere()
    {
        Assert.Equal(Path.Combine("/u", "Library", "Application Support", "aipod"), HomeResolver.NewHomeFor(OsKinds.MacOs, "/u"));
        Assert.Equal(Path.Combine("/u", ".aipod"), HomeResolver.NewHomeFor(OsKinds.Windows, "/u"));
        Assert.Equal(Path.Combine("/u", ".aipod"), HomeResolver.NewHomeFor(OsKinds.Linux, "/u"));
        Assert.Equal(AgentConfig.HomeFor(OsKinds.MacOs, "/u"), HomeResolver.OldHomeFor(OsKinds.MacOs, "/u"));
        Assert.Equal(AgentConfig.HomeFor(OsKinds.Windows, "/u"), HomeResolver.OldHomeFor(OsKinds.Windows, "/u"));
    }

    [Fact]
    public void The_marker_files_are_the_two_the_agent_really_uses()
    {
        var config = AgentConfig.FromEnvironment(k => k == "AP_AGENT_HOME" ? "/h" : null);
        Assert.Equal(HomeResolver.Markers, [Path.GetFileName(config.IdentityPath), Path.GetFileName(config.DatabasePath)]);
    }

    [Theory]
    [InlineData(OsKinds.MacOs)]
    [InlineData(OsKinds.Windows)]
    [InlineData(OsKinds.Linux)]
    public void A_machine_with_no_agent_files_anywhere_gets_the_aipod_folder(string os) =>
        Assert.Equal(HomeResolver.NewHomeFor(os, "/u"), HomeResolver.Resolve(os, "/u", Files()));

    [Theory]
    [InlineData(OsKinds.MacOs, "agent.json")]
    [InlineData(OsKinds.MacOs, "agent.db")]
    [InlineData(OsKinds.Windows, "agent.json")]
    [InlineData(OsKinds.Linux, "agent.db")]
    public void A_machine_that_already_uses_the_old_folder_keeps_it_whichever_file_shows_it(string os, string marker)
    {
        var old = HomeResolver.OldHomeFor(os, "/u");
        Assert.Equal(old, HomeResolver.Resolve(os, "/u", Files(Path.Combine(old, marker))));
    }

    [Fact]
    public void The_aipod_folder_wins_when_it_has_agent_files_even_if_the_old_one_has_too()
    {
        var fresh = HomeResolver.NewHomeFor(OsKinds.Linux, "/u");
        var old = HomeResolver.OldHomeFor(OsKinds.Linux, "/u");

        Assert.Equal(fresh, HomeResolver.Resolve(OsKinds.Linux, "/u", Files(Path.Combine(fresh, "agent.json"))));
        Assert.Equal(fresh, HomeResolver.Resolve(OsKinds.Linux, "/u", Files(Path.Combine(fresh, "agent.db"), Path.Combine(old, "agent.json"), Path.Combine(old, "agent.db"))));
    }

    [Fact]
    public void Files_that_are_not_the_agents_own_never_move_the_home()
    {
        var fresh = HomeResolver.NewHomeFor(OsKinds.Linux, "/u");
        var old = HomeResolver.OldHomeFor(OsKinds.Linux, "/u");

        // an empty or stray aipod folder next to a working old one: the old one stays the home
        Assert.Equal(old, HomeResolver.Resolve(OsKinds.Linux, "/u", Files(Path.Combine(fresh, "agent.log"), Path.Combine(fresh, "tray.lock"), Path.Combine(old, "agent.json"))));
        // stray files in the old folder alone are not an agent
        Assert.Equal(fresh, HomeResolver.Resolve(OsKinds.Linux, "/u", Files(Path.Combine(old, "agent.log"))));
    }

    [Fact]
    public void Resolving_needs_a_way_to_look()
    {
        Assert.Throws<ArgumentNullException>(() => HomeResolver.Resolve(OsKinds.Linux, "/u", null!));
    }

    [Fact]
    public void A_variable_wins_over_the_resolver_and_without_one_the_resolver_decides_and_nothing_is_migrated_from_it()
    {
        var env = new Dictionary<string, string?> { ["AP_AGENT_HOME"] = "/explicit" };
        var explicitHome = AgentConfig.FromEnvironment(k => env.GetValueOrDefault(k), fileExists: _ => true);
        Assert.Equal("/explicit", explicitHome.Home);
        Assert.Null(explicitHome.MigrateFrom);

        env["AP_AGENT_HOME"] = null;
        env["AP_AGENT_HOME"] = "/new-name"; // the new variable name works too
        Assert.Equal("/new-name", AgentConfig.FromEnvironment(k => env.GetValueOrDefault(k)).Home);

        env["AP_AGENT_HOME"] = null;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var old = HomeResolver.OldHomeFor(AgentConfig.Os, profile);
        var resolved = AgentConfig.FromEnvironment(k => env.GetValueOrDefault(k), fileExists: path => path == Path.Combine(old, "agent.json"));
        Assert.Equal(old, resolved.Home);
        Assert.Equal(HomeResolver.NewHomeFor(AgentConfig.Os, profile), AgentConfig.FromEnvironment(k => env.GetValueOrDefault(k), fileExists: _ => false).Home);
        Assert.Contains(AgentConfig.DefaultHome(), new[] { HomeResolver.NewHomeFor(AgentConfig.Os, profile), old });
    }
}

/// <summary>ADR-0013 step 2 (tier 1): what people see says aipod; what the system keys on keeps its name.</summary>
public sealed class DisplayNameTests
{
    [Fact]
    public void The_windows_service_shows_aipod_agent_in_the_services_list_and_keeps_its_service_name()
    {
        var args = WindowsServiceSetup.Create(ServiceLayout.For(OsKinds.Windows));
        Assert.Equal("aipod agent", args[^1]);
        Assert.Equal("DisplayName=", args[^2]);
        Assert.Equal("aipod", args[1]);
        Assert.StartsWith("aipod agent:", WindowsServiceSetup.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_service_units_and_the_account_describe_themselves_as_aipod_with_unchanged_identifiers()
    {
        var linux = SystemdUnit.Render(ServiceLayout.For(OsKinds.Linux));
        Assert.Contains("Description=aipod agent", linux, StringComparison.Ordinal);
        Assert.Contains("aipod", linux, StringComparison.Ordinal);
        Assert.Equal("aipod", ServiceLayout.ServiceName);
        Assert.Equal("com.aipod.agent", LaunchdDaemon.Label);
        Assert.Equal("com.aipod.agent.user", StandaloneLayout.Label);
        Assert.Equal("aipod", AgentConfig.CredentialService); // never changes: the Keychain service and the DPAPI entropy
    }

    [Fact]
    public void The_standalone_agent_is_named_aipod_in_the_bundle_the_user_unit_and_the_logon_task()
    {
        var plist = StandaloneUnits.RenderInfoPlist("0.5.0");
        Assert.Contains("<key>CFBundleName</key><string>aipod</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>CFBundleDisplayName</key><string>aipod</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>CFBundleIdentifier</key><string>com.aipod.agent.app</string>", plist, StringComparison.Ordinal);

        var layout = StandaloneLayout.For(OsKinds.Linux, "/home/u", "/home/u/.aipod", "u");
        Assert.Contains("Description=aipod agent (standalone, this user)", StandaloneUnits.RenderSystemd(layout), StringComparison.Ordinal);
        var windows = StandaloneLayout.For(OsKinds.Windows, @"C:\Users\u", @"C:\Users\u\.aipod", "u");
        Assert.Contains("<Description>aipod agent (standalone, this user)</Description>", StandaloneUnits.RenderLogonTask(windows), StringComparison.Ordinal);
    }
}
