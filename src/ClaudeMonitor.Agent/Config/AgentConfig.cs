using System.Reflection;

namespace ClaudeMonitor.Agent.Config;

public static class PushScopes
{
    public const string Session = "session";
    public const string Machine = "machine";
}

/// <summary>
/// The agent's ONE configuration module (global #2): the only place that reads the environment or holds a path,
/// URL, interval or limit. Every other file takes an <see cref="AgentConfig"/>. Every variable is in .env.example.
/// </summary>
public sealed record AgentConfig
{
    /// <summary>The user-only directory holding the local database, the lock and the agent's identity file.</summary>
    public required string Home { get; init; }

    /// <summary>
    /// The folder an older agent used as its default home, set only when <see cref="Home"/> is the default and an older
    /// default exists (Windows: %LOCALAPPDATA%\ClaudeMonitor). <see cref="HomeMigration"/> copies the identity file from it.
    /// Null when CM_AGENT_HOME is set: an explicit home is never migrated.
    /// </summary>
    public string? MigrateFrom { get; init; }

    /// <summary>The API's origin when none is saved yet (set by "cm-agent login --server").</summary>
    public string? ServerOverride { get; init; }

    /// <summary>"keychain" (default: macOS Keychain / Windows Credential Manager) or "file" (tests only).</summary>
    public string CredentialStore { get; init; } = "keychain";

    public TimeSpan PermissionWait { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan StopWait { get; init; } = TimeSpan.Zero;

    /// <summary>True when CM_STOP_WAIT was set: it then wins over the value `cm-agent install --stop-wait` saved.</summary>
    public bool StopWaitFromEnvironment { get; init; }

    /// <summary>The longest a hook may wait for the web (Claude Code's hook timeout is raised by the same amount).</summary>
    public const int WaitMaxSeconds = 590;
    /// <summary>
    /// Push into an idle session (ADR-0003): "claude/channel" notifications from `cm-agent mcp`. Off by default; `cm-agent install
    /// --push on` saves it, and CM_PUSH (on/off), when set, wins.
    /// </summary>
    public bool PushEnabled { get; init; }

    /// <summary>True when CM_PUSH was set: it then wins over the value `cm-agent install --push` saved.</summary>
    public bool PushFromEnvironment { get; init; }

    /// <summary>"session" (default): only prompts addressed to this session. "machine": any prompt of this machine (see ADR-0003).</summary>
    public string PushScope { get; init; } = PushScopes.Session;

    /// <summary>How often the MCP process looks in the local database for a prompt to push (a file read, no model, no network).</summary>
    public TimeSpan PushPollEvery { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>A pushed prompt that never shows up in the transcript within this long goes back to the hooks (the channel was not enabled).</summary>
    public TimeSpan PushConfirmWait { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>The longest message body pushed whole; a longer one is cut with a marker.</summary>
    public int PushContentMax { get; init; } = 8000;

    /// <summary>The API pings the stream every 20 s; a stream silent for this long is dead (a half-open connection) and is reopened.</summary>
    public TimeSpan StreamIdleTimeout { get; init; } = TimeSpan.FromSeconds(75);

    public TimeSpan FlushEvery { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan HeartbeatEvery { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan SettingsEvery { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan TranscriptEvery { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan PollEvery { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan DaemonStartWait { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan RetryMax { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long one login request may take; the API answers at once, so a longer wait is a dead connection.</summary>
    public TimeSpan LoginRequestTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long one of the daemon's API calls may take (all but the stream, which stays open). Longer than the login's:
    /// a full batch (<see cref="BatchBytes"/>) must still go up on a slow uplink, or it would be resent for ever.
    /// </summary>
    public TimeSpan ApiCallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Added to the login's polling interval after each failed poll (RFC 8628 §3.5), up to <see cref="LoginPollMax"/>.</summary>
    public TimeSpan LoginBackoffStep { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan LoginPollMax { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A pooled connection is retired after this long, so one opened before a network change is not reused for ever.</summary>
    public static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ConnectionIdle = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The head start of one address before the next is tried beside it (RFC 8305 §5 recommends 250 ms).</summary>
    public static readonly TimeSpan ConnectStagger = TimeSpan.FromMilliseconds(250);
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

    public static AgentConfig FromEnvironment(Func<string, string?>? read = null, Func<string?>? legacyHome = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        legacyHome ??= LegacyHome;
        static TimeSpan Seconds(string? value, TimeSpan fallback, int max) =>
            int.TryParse(value, out var s) && s >= 0 ? TimeSpan.FromSeconds(Math.Min(s, max)) : fallback;

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
            PushEnabled = read("CM_PUSH") == "on",
            PushFromEnvironment = read("CM_PUSH") is { Length: > 0 },
            PushScope = read("CM_PUSH_SCOPE") == PushScopes.Machine ? PushScopes.Machine : PushScopes.Session,
        };
    }

    /// <summary>
    /// macOS: ~/Library/Application Support/ClaudeMonitor; Windows: %USERPROFILE%\.claude-monitor. Not under AppData: the
    /// Claude desktop app is a packaged (MSIX) app, and every process it starts sees %LOCALAPPDATA% redirected to a private
    /// copy, so a login made in a normal terminal was invisible to its hooks and MCP server.
    /// </summary>
    public static string DefaultHome() => HomeFor(OperatingSystem.IsWindows(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The older Windows default (%LOCALAPPDATA%\ClaudeMonitor), kept only to migrate from and to find old installs; null on macOS.</summary>
    public static string? LegacyHome() =>
        OperatingSystem.IsWindows() ? LegacyHomeFor(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) : null;

    public static string HomeFor(bool windows, string userProfile) =>
        windows ? Path.Combine(userProfile, ".claude-monitor") : Path.Combine(userProfile, "Library", "Application Support", "ClaudeMonitor");

    public static string LegacyHomeFor(string localAppData) => Path.Combine(localAppData, "ClaudeMonitor");

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
