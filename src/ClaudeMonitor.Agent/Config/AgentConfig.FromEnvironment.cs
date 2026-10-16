using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Config;

/// <summary>How the configuration is read from the environment (every variable is in .env.example) and where the home is.</summary>
public sealed partial record AgentConfig
{
    public static AgentConfig FromEnvironment(Func<string, string?>? read = null, Func<string?>? legacyHome = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        legacyHome ??= LegacyHome;
        static TimeSpan Seconds(string? value, TimeSpan fallback, int max) =>
            int.TryParse(value, out var s) && s >= 0 ? TimeSpan.FromSeconds(Math.Min(s, max)) : fallback;
        static TimeSpan Between(string? value, TimeSpan fallback, int min, int max) =>
            int.TryParse(value, out var s) ? TimeSpan.FromSeconds(Math.Clamp(s, min, max)) : fallback;

        var explicitHome = read("CM_AGENT_HOME") is { Length: > 0 };
        return new AgentConfig
        {
            Home = explicitHome ? read("CM_AGENT_HOME")! : DefaultHome(),
            MigrateFrom = explicitHome ? null : legacyHome(),
            ServerOverride = read("CM_SERVER") is { Length: > 0 } s ? s.TrimEnd('/') : null,
            CredentialStore = read("CM_CREDENTIALS") == "file" ? "file" : "keychain",
            PermissionWait = Seconds(read("CM_PERMISSION_WAIT"), TimeSpan.FromSeconds(120), WaitMaxSeconds),
            StopWait = Seconds(read("CM_STOP_WAIT"), TimeSpan.Zero, WaitMaxSeconds),
            StopWaitFromEnvironment = read("CM_STOP_WAIT") is { Length: > 0 },
            PushEnabled = PushTransports.Enables(read("CM_PUSH")),
            PushFromEnvironment = read("CM_PUSH") is { Length: > 0 },
            PushTransport = PushTransports.Normalize(read("CM_PUSH")),
            BinaryOverride = read("CM_BINARY") is { Length: > 0 } binary ? binary : null,
            PluginId = read("CM_PLUGIN_ID") is { Length: > 0 } pluginId ? pluginId : null,
            PluginDataDir = read("CLAUDE_PLUGIN_DATA") is { Length: > 0 } pluginData ? pluginData : null,
            MessagingSocket = read("CLAUDE_CODE_MESSAGING_SOCKET") is { Length: > 0 } socket ? socket : null,
            MessagingToken = read("CLAUDE_CODE_MESSAGING_TOKEN") is { Length: > 0 } token ? new SecretText(token) : null,
            ClaudeUpdateEnabled = read("CM_CLAUDE_UPDATE") == "on",
            ClaudeUpdateFromEnvironment = read("CM_CLAUDE_UPDATE") is { Length: > 0 },
            ClaudeConfigDir = read("CLAUDE_CONFIG_DIR") is { Length: > 0 } claudeDir ? claudeDir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
            ClaudeBinary = read("CM_CLAUDE_BINARY") is { Length: > 0 } claudeBinary ? claudeBinary : null,
            PathVariable = read("PATH") ?? "",
            AutoUpdate = UpdateModes.Normalize(read("CM_AUTO_UPDATE")),
            UpdateHealthWait = Seconds(read("CM_UPDATE_HEALTH_WAIT"), TimeSpan.FromSeconds(90), 600),
            AutoUpdateFromEnvironment = read("CM_AUTO_UPDATE") is { Length: > 0 },
            PushScope = read("CM_PUSH_SCOPE") == PushScopes.Machine ? PushScopes.Machine : PushScopes.Session,
            ServiceMode = read("CM_SERVICE") == "1",
            MachinesContextEnabled = read("CM_MACHINES_CONTEXT") != "off",
            MachineCacheEvery = Between(read("CM_MACHINE_CACHE_EVERY"), TimeSpan.FromMinutes(5), 30, 3600),
            PresenceProbeEvery = Between(read("CM_PRESENCE_PROBE_EVERY"), TimeSpan.FromHours(6), 60, 86_400),
        };
    }

    /// <summary>
    /// macOS: ~/Library/Application Support/ClaudeMonitor; Windows: %USERPROFILE%\.claude-monitor. Not under AppData: the
    /// Claude desktop app is a packaged (MSIX) app, and every process it starts sees %LOCALAPPDATA% redirected to a private
    /// copy, so a login made in a normal terminal was invisible to its hooks and MCP server.
    /// </summary>
    public static string DefaultHome() => HomeFor(Os, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The older Windows default (%LOCALAPPDATA%\ClaudeMonitor), kept only to migrate from and to find old installs; null on macOS.</summary>
    public static string? LegacyHome() =>
        OperatingSystem.IsWindows() ? LegacyHomeFor(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) : null;

    public static string HomeFor(bool windows, string userProfile) => HomeFor(windows ? OsKinds.Windows : OsKinds.MacOs, userProfile);

    /// <summary>macOS keeps the Library folder; Windows and Linux use a dot folder in the user's profile.</summary>
    public static string HomeFor(string os, string userProfile) =>
        os == OsKinds.MacOs ? Path.Combine(userProfile, "Library", "Application Support", "ClaudeMonitor") : Path.Combine(userProfile, ".claude-monitor");

    public static string LegacyHomeFor(string localAppData) => Path.Combine(localAppData, "ClaudeMonitor");
}
