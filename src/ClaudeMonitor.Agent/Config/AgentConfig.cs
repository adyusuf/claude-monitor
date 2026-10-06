using System.Reflection;
using System.Runtime.InteropServices;
using ClaudeMonitor.Contracts;

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
public sealed partial record AgentConfig
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

    /// <summary>
    /// How far this machine lets the agent go about updating itself (ADR-0004): "off" (default: no update call at all),
    /// "check" (look and report) or "on" (install too). `cm-agent config auto-update` saves it, CM_AUTO_UPDATE wins when set.
    /// The workspace has its own cap; the lower of the two applies. An explicit `cm-agent update` is not an automatic action and ignores both.
    /// </summary>
    public string AutoUpdate { get; init; } = UpdateModes.Off;

    /// <summary>True when CM_AUTO_UPDATE was set: it then wins over the value `cm-agent config` saved.</summary>
    public bool AutoUpdateFromEnvironment { get; init; }

    /// <summary>The channel this build belongs to ("test", "prod", or "dev" for an unsigned local build), set at build time.</summary>
    public string UpdateChannel { get; init; } = BuildMetadata("UpdateChannel") is { Length: > 0 } channel ? channel : "dev";

    /// <summary>The public key (base64 SubjectPublicKeyInfo, ECDSA P-256) every update must be signed with; empty in a build without one, which then refuses all updates.</summary>
    public string UpdatePublicKey { get; init; } = BuildMetadata("UpdatePublicKey") ?? "";

    /// <summary>The OS and CPU the update is asked for and signed for; the host's own, except where a test stands in for a supported one.</summary>
    public string UpdateOs { get; init; } = Os;

    public string UpdateArch { get; init; } = Arch;

    /// <summary>How long a downloaded candidate (and macOS's codesign check of it) may take to answer.</summary>
    public TimeSpan UpdateProbeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether this machine lets the agent update Claude Code itself (ADR-0006; off by default). `cm-agent config claude-update on|off`
    /// saves it, CM_CLAUDE_UPDATE wins when set. The workspace must allow it too; both are needed.
    /// </summary>
    public bool ClaudeUpdateEnabled { get; init; }

    /// <summary>True when CM_CLAUDE_UPDATE was set: it then wins over the value `cm-agent config` saved.</summary>
    public bool ClaudeUpdateFromEnvironment { get; init; }

    /// <summary>Where Claude Code keeps its per-session files (sessions/&lt;pid&gt;.json): CLAUDE_CONFIG_DIR, else ~/.claude.</summary>
    public string ClaudeConfigDir { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>The `claude` to update when it must not be searched for (CM_CLAUDE_BINARY; tests); null = the first `claude` on <see cref="PathVariable"/>.</summary>
    public string? ClaudeBinary { get; init; }

    public string PathVariable { get; init; } = Environment.GetEnvironmentVariable("PATH") ?? "";

    /// <summary>The shortest time between two attempts to update Claude Code (`claude update` has no dry run, so each attempt is a real one).</summary>
    public TimeSpan ClaudeUpdateEvery { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Every live session must have been idle at least this long.</summary>
    public TimeSpan ClaudeIdleFor { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The warning before `claude update` runs; `cm-agent claude-update cancel` stops it.</summary>
    public TimeSpan ClaudeCountdown { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>A cancelled attempt is not made again for this long.</summary>
    public TimeSpan ClaudeSnooze { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan ClaudeUpdateTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan ClaudeCountdownPoll { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The shortest time between two looks at the server for an update.</summary>
    public TimeSpan UpdateCheckEvery { get; init; } = TimeSpan.FromHours(6);

    /// <summary>How often the daemon's update chore wakes to see whether a look is due (and whether the setting changed).</summary>
    public TimeSpan UpdatePollEvery { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>After a failed or rolled-back automatic update the daemon waits this long before it tries again.</summary>
    public TimeSpan UpdateRetryAfter { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The new daemon has this long to be answered by the API (or to be shown unreachable) before the update is judged.</summary>
    public TimeSpan UpdateHealthWait { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>How long a running daemon gets to stop when an update (or a rollback) asks it to.</summary>
    public TimeSpan UpdateStopWait { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan UpdateDownloadTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The largest download accepted; the published zips are far smaller (a guard against an endless body).</summary>
    public long UpdateDownloadMax { get; init; } = 250L * 1024 * 1024;

    /// <summary>The largest binary taken out of a download (a guard against a zip bomb).</summary>
    public long UpdateBinaryMax { get; init; } = 400L * 1024 * 1024;

    /// <summary>How often a running daemon looks for a stop request (the file <see cref="StopRequestPath"/>).</summary>
    public TimeSpan StopPollEvery { get; init; } = TimeSpan.FromSeconds(1);

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
    public string BinaryPath => Path.Combine(Home, "bin", OperatingSystem.IsWindows() ? "cm-agent.exe" : "cm-agent");

    /// <summary>What the last update check and install left behind (shown by `cm-agent status`).</summary>
    public string UpdateStatePath => Path.Combine(Home, "update-state.json");
    public string UpdateLockPath => Path.Combine(Home, "update.lock");
    public string UpdateDir => Path.Combine(Home, "update");

    /// <summary>Written by an update to ask the running daemon to stop; the daemon deletes it and exits.</summary>
    public string PidPath => Path.Combine(Home, "daemon.pid");
    public string ClaudeUpdateStatePath => Path.Combine(Home, "claude-update-state.json");
    public string ClaudeUpdateLockPath => Path.Combine(Home, "claude-update.lock");

    /// <summary>Written by `cm-agent claude-update cancel`; the countdown sees it and stands down.</summary>
    public string ClaudeCancelPath => Path.Combine(Home, "claude-update.cancel");
    public string StopRequestPath => Path.Combine(Home, "daemon.stop");

    public const string CredentialService = "claude-monitor-agent";
    public const string PluginName = "monitor-agent";
    public const string MarketplaceName = "monitor-agent-local";

    public static string Version =>
        typeof(AgentConfig).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";

    /// <summary>The CPU code the API knows ("arm64" / "x64").</summary>
    public static string Arch => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    private static string? BuildMetadata(string key) =>
        typeof(AgentConfig).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>The OS code the API knows (<see cref="OsKinds"/>); anything else is refused at login.</summary>
    public static string Os =>
        OperatingSystem.IsWindows() ? OsKinds.Windows
        : OperatingSystem.IsMacOS() ? OsKinds.MacOs
        : OperatingSystem.IsLinux() ? OsKinds.Linux
        : Unsupported;

    public const string Unsupported = "unsupported";

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
