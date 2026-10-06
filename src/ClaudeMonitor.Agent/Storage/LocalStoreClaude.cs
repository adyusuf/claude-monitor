namespace ClaudeMonitor.Agent.Storage;

/// <summary>
/// The Claude Code config folders sessions run under (ADR-0006): each hook records its session's CLAUDE_CONFIG_DIR (or the
/// default), so the Claude updater can see the sessions of every folder, not only of the one the daemon was started with.
/// </summary>
public sealed partial class LocalStore
{
    private void CreateClaudeTables() => Exec("""
        CREATE TABLE IF NOT EXISTS claude_config_dirs (dir TEXT PRIMARY KEY, seen_at TEXT NOT NULL);
        """);

    /// <summary>A hook says: a session runs under this config folder now.</summary>
    public void ClaudeConfigDirSeen(string dir, DateTimeOffset now) =>
        Exec("""
            INSERT INTO claude_config_dirs (dir, seen_at) VALUES ($d, $t)
            ON CONFLICT(dir) DO UPDATE SET seen_at = excluded.seen_at
            """, ("$d", NormalizeDir(dir)), ("$t", Iso(now)));

    /// <summary>The folders a hook recorded at or after <paramref name="since"/>, oldest first.</summary>
    public List<string> ClaudeConfigDirsSince(DateTimeOffset since)
    {
        var dirs = new List<(string Dir, DateTimeOffset At)>();
        using (var cmd = Command("SELECT dir, seen_at FROM claude_config_dirs", null))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read()) dirs.Add((r.GetString(0), At(r.GetString(1))));
        }

        return [.. dirs.Where(d => d.At >= since).OrderBy(d => d.At).Select(d => d.Dir)];
    }

    /// <summary>One spelling per folder: absolute, without a trailing separator.</summary>
    public static string NormalizeDir(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
}
