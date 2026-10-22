using System.Globalization;
using System.Text.RegularExpressions;
using Podium.Agent.ClaudeUpdate;
using Podium.Agent.Config;
using Podium.Agent.Exec;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Daemon;

/// <summary>
/// What the agent reports about Claude Code on its machine (ADR-0008 §3e), from what it already detects for ADR-0006: the install
/// (<see cref="ClaudeLocator"/>), its version (<c>claude --version</c>, kept in the local database and re-read when the binary's
/// mtime changes or <see cref="AgentConfig.PresenceProbeEvery"/> has passed) and the live sessions with how many are idle.
/// The path is reported home-relative. Nothing here changes anything on the machine.
/// </summary>
public static partial class PresenceProbe
{
    public const string VersionKey = "presence.claude_version";
    public const string VersionPathKey = "presence.claude_path";
    public const string VersionMtimeKey = "presence.claude_mtime";
    public const string VersionAtKey = "presence.claude_at";
    private static readonly TimeSpan SessionsSeenWithin = TimeSpan.FromDays(1);

    [GeneratedRegex(@"\d+\.\d+\.\d+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionPattern();

    public static ClaudePresence Probe(AgentConfig config, LocalStore store, Func<string, string?> version, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(version);
        var install = ClaudeLocator.Locate(config);
        var kind = install.Kind switch
        {
            ClaudeKind.Npm => ClaudeKinds.Npm,
            ClaudeKind.Native => ClaudeKinds.Native,
            ClaudeKind.Unsupported => ClaudeKinds.Unsupported,
            _ => ClaudeKinds.None,
        };
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = install.Path is { } p ? ToolInventory.HomeRelative(p, profile) : null;
        var v = install.Path is { } real ? CachedVersion(config, store, real, version, now) : null;
        var (live, idle) = Sessions(config, store, now);
        return new ClaudePresence(kind, path, v, live, idle, now);
    }

    /// <summary>Live sessions across every Claude config folder the hooks have seen, and how many of them say "idle".</summary>
    public static (int Live, int Idle) Sessions(AgentConfig config, LocalStore store, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        int live = 0, idle = 0;
        foreach (var dir in ClaudeSessions.ConfigDirs(config, store, now, SessionsSeenWithin))
        {
            var view = ClaudeSessions.Read(dir);
            live += view.Live.Count;
            idle += view.Live.Count(s => s.Status == "idle");
        }

        return (live, idle);
    }

    private static string? CachedVersion(AgentConfig config, LocalStore store, string real, Func<string, string?> version, DateTimeOffset now)
    {
        var mtime = Mtime(real);
        var at = DateTimeOffset.TryParse(store.Get(VersionAtKey), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : (DateTimeOffset?)null;
        var fresh = store.Get(VersionPathKey) == real && store.Get(VersionMtimeKey) == mtime && at is { } a && now - a < config.PresenceProbeEvery && now >= a;
        if (fresh) return store.Get(VersionKey) is { Length: > 0 } cached ? cached : null;
        var read = version(real) is { } text && VersionPattern().Match(text) is { Success: true } m ? m.Value : null;
        store.SetMany([(VersionKey, read ?? ""), (VersionPathKey, real), (VersionMtimeKey, mtime), (VersionAtKey, now.ToString("O", CultureInfo.InvariantCulture))]);
        return read;
    }

    private static string Mtime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path).ToString("O", CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
