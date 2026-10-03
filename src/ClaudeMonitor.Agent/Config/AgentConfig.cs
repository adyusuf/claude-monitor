using System.Reflection;

namespace ClaudeMonitor.Agent.Config;

/// <summary>
/// The agent's ONE configuration module (global #2): the only place that reads the environment or holds a path,
/// URL, interval or limit. Every other file takes an <see cref="AgentConfig"/>. Every variable is in .env.example.
/// </summary>
public sealed record AgentConfig
{
    /// <summary>The user-only directory holding the local database, the lock and the agent's identity file.</summary>
    public required string Home { get; init; }

    /// <summary>The API's origin when none is saved yet (set by "cm-agent login --server").</summary>
    public string? ServerOverride { get; init; }

    /// <summary>"keychain" (default: macOS Keychain / Windows Credential Manager) or "file" (tests only).</summary>
    public string CredentialStore { get; init; } = "keychain";

    public TimeSpan PermissionWait { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan StopWait { get; init; } = TimeSpan.Zero;
    public TimeSpan FlushEvery { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan HeartbeatEvery { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan SettingsEvery { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan TranscriptEvery { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan PollEvery { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan DaemonStartWait { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan RetryMax { get; init; } = TimeSpan.FromMinutes(2);
    public int BatchEvents { get; init; } = 200;
    public int BatchBytes { get; init; } = 4 * 1024 * 1024;
    public int EventMaxBytesDefault { get; init; } = 262_144;
    public int TranscriptLineMax { get; init; } = 1024 * 1024;

    public string DatabasePath => Path.Combine(Home, "agent.db");
    public string LockPath => Path.Combine(Home, "daemon.lock");
    public string IdentityPath => Path.Combine(Home, "agent.json");
    public string PluginDir => Path.Combine(Home, "claude-plugin");
    public string LogPath => Path.Combine(Home, "agent.log");

    public const string CredentialService = "claude-monitor-agent";
    public const string PluginName = "monitor-agent";
    public const string MarketplaceName = "monitor-agent-local";

    public static string Version =>
        typeof(AgentConfig).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";

    /// <summary>The OS code the API knows ("macos" / "windows"); anything else is refused at login.</summary>
    public static string Os => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "unsupported";

    public static AgentConfig FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        static TimeSpan Seconds(string? value, TimeSpan fallback, int max) =>
            int.TryParse(value, out var s) && s >= 0 ? TimeSpan.FromSeconds(Math.Min(s, max)) : fallback;

        var home = read("CM_AGENT_HOME") is { Length: > 0 } h ? h : DefaultHome();
        return new AgentConfig
        {
            Home = home,
            ServerOverride = read("CM_SERVER") is { Length: > 0 } s ? s.TrimEnd('/') : null,
            CredentialStore = read("CM_CREDENTIALS") == "file" ? "file" : "keychain",
            PermissionWait = Seconds(read("CM_PERMISSION_WAIT"), TimeSpan.FromSeconds(120), 590),
            StopWait = Seconds(read("CM_STOP_WAIT"), TimeSpan.Zero, 590),
        };
    }

    /// <summary>macOS: ~/Library/Application Support/ClaudeMonitor; Windows: %LOCALAPPDATA%\ClaudeMonitor.</summary>
    public static string DefaultHome() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeMonitor")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support",
                "ClaudeMonitor");

    /// <summary>Creates the home directory readable by the user only.</summary>
    public void EnsureHome()
    {
        Directory.CreateDirectory(Home);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
