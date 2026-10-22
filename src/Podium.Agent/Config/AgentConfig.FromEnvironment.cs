using Podium.Contracts;

namespace Podium.Agent.Config;

/// <summary>How the configuration is read from the environment (every variable is in .env.example) and where the home is.</summary>
public sealed partial record AgentConfig
{
    public static AgentConfig FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        static TimeSpan Seconds(string? value, TimeSpan fallback, int max) =>
            int.TryParse(value, out var s) && s >= 0 ? TimeSpan.FromSeconds(Math.Min(s, max)) : fallback;
        static TimeSpan Between(string? value, TimeSpan fallback, int min, int max) =>
            int.TryParse(value, out var s) ? TimeSpan.FromSeconds(Math.Clamp(s, min, max)) : fallback;

        var explicitHome = read("AP_AGENT_HOME") is { Length: > 0 };
        return new AgentConfig
        {
            Home = explicitHome ? read("AP_AGENT_HOME")! : DefaultHome(),
            ServerOverride = read("AP_SERVER") is { Length: > 0 } s ? s.TrimEnd('/') : null,
            CredentialStore = read("AP_CREDENTIALS") == "file" ? "file" : "keychain",
            PermissionWait = Seconds(read("AP_PERMISSION_WAIT"), TimeSpan.FromSeconds(120), WaitMaxSeconds),
            StopWait = Seconds(read("AP_STOP_WAIT"), TimeSpan.Zero, WaitMaxSeconds),
            StopWaitFromEnvironment = read("AP_STOP_WAIT") is { Length: > 0 },
            PushEnabled = PushTransports.Enables(read("AP_PUSH")),
            PushFromEnvironment = read("AP_PUSH") is { Length: > 0 },
            PushTransport = PushTransports.Normalize(read("AP_PUSH")),
            InboxFormat = InboxFormats.Normalize(read("AP_INBOX_FORMAT")),
            InboxFormatFromEnvironment = read("AP_INBOX_FORMAT") is { Length: > 0 },
            BinaryOverride = read("AP_BINARY") is { Length: > 0 } binary ? binary : null,
            PluginId = read("AP_PLUGIN_ID") is { Length: > 0 } pluginId ? pluginId : null,
            PluginDataDir = read("CLAUDE_PLUGIN_DATA") is { Length: > 0 } pluginData ? pluginData : null,
            PluginStopWaitMax = Seconds(read("AP_PLUGIN_STOP_WAIT_MAX"), TimeSpan.Zero, WaitMaxSeconds),
            MessagingSocket = read("CLAUDE_CODE_MESSAGING_SOCKET") is { Length: > 0 } socket ? socket : null,
            MessagingToken = read("CLAUDE_CODE_MESSAGING_TOKEN") is { Length: > 0 } token ? new SecretText(token) : null,
            ClaudeUpdateEnabled = read("AP_CLAUDE_UPDATE") == "on",
            ClaudeUpdateFromEnvironment = read("AP_CLAUDE_UPDATE") is { Length: > 0 },
            ClaudeConfigDir = read("CLAUDE_CONFIG_DIR") is { Length: > 0 } claudeDir ? claudeDir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
            ClaudeBinary = read("AP_CLAUDE_BINARY") is { Length: > 0 } claudeBinary ? claudeBinary : null,
            PathVariable = read("PATH") ?? "",
            AutoUpdate = UpdateModes.Normalize(read("AP_AUTO_UPDATE")),
            UpdateHealthWait = Seconds(read("AP_UPDATE_HEALTH_WAIT"), TimeSpan.FromSeconds(90), 600),
            AutoUpdateFromEnvironment = read("AP_AUTO_UPDATE") is { Length: > 0 },
            PushScope = read("AP_PUSH_SCOPE") == PushScopes.Machine ? PushScopes.Machine : PushScopes.Session,
            ServiceMode = read("AP_SERVICE") == "1",
            Standalone = read("AP_STANDALONE") == "1",
            MachinesContextEnabled = read("AP_MACHINES_CONTEXT") != "off",
            MachineCacheEvery = Between(read("AP_MACHINE_CACHE_EVERY"), TimeSpan.FromMinutes(5), 30, 3600),
            PresenceProbeEvery = Between(read("AP_PRESENCE_PROBE_EVERY"), TimeSpan.FromHours(6), 60, 86_400),
            AsksEnabled = !string.Equals(read("AP_ASKS"), "off", StringComparison.OrdinalIgnoreCase),
            AskIdleFor = Between(read("AP_ASK_IDLE_FOR"), TimeSpan.FromSeconds(30), 5, 600),
            AskConfirmWait = Between(read("AP_ASK_CONFIRM_WAIT"), TimeSpan.FromMinutes(2), 30, 600),
            ProjectFactsEvery = Between(read("AP_PROJECT_FACTS_EVERY"), TimeSpan.FromMinutes(5), 30, 3600),
            ExecScriptOutputCeiling = long.TryParse(read("AP_EXEC_SCRIPT_OUTPUT_CEILING"), out var ceiling) ? Math.Clamp(ceiling, 1024L * 1024, 64L * 1024 * 1024) : 64L * 1024 * 1024,
        };
    }

    /// <summary>
    /// The home when no variable names one (ADR-0016): macOS ~/Library/Application Support/aipod; Windows and Linux ~/.aipod. Not under AppData: the Claude
    /// desktop app is a packaged (MSIX) app, and every process it starts sees %LOCALAPPDATA% redirected to a private copy, so a login made in a normal
    /// terminal was invisible to its hooks and MCP server.
    /// </summary>
    public static string DefaultHome() => HomeFor(Os, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string HomeFor(bool windows, string userProfile) => HomeFor(windows ? OsKinds.Windows : OsKinds.MacOs, userProfile);

    /// <summary>macOS keeps the Library folder; Windows and Linux use a dot folder in the user's profile.</summary>
    public static string HomeFor(string os, string userProfile) =>
        os == OsKinds.MacOs ? Path.Combine(userProfile, "Library", "Application Support", "aipod") : Path.Combine(userProfile, ".aipod");
}
