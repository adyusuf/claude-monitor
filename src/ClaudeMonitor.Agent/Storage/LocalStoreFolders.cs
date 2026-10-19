namespace ClaudeMonitor.Agent.Storage;

/// <summary>
/// The project folders sessions worked in on this machine (ADR-0008 §3a′), so the daemon can read their commit and tag the way it reads a listed folder's. The path
/// stays in this user-only database; the API only ever gets the project key and an HMAC of the path. A folder nobody has visited for a month is forgotten.
/// </summary>
public sealed partial class LocalStore
{
    public const int FoldersMax = 50;

    private void CreateFolderTables() => Exec("CREATE TABLE IF NOT EXISTS visited_folders (project_key TEXT PRIMARY KEY, folder TEXT NOT NULL, seen_at TEXT NOT NULL);");

    /// <summary>A hook says: a session of this project works in that folder. A hook that says what is known writes nothing, except once an hour (it runs on every tool call).</summary>
    public void SeeFolder(string projectKey, string folder, DateTimeOffset now) =>
        Exec("""
            INSERT INTO visited_folders (project_key, folder, seen_at) VALUES ($k, $f, $t)
            ON CONFLICT(project_key) DO UPDATE SET folder = excluded.folder, seen_at = excluded.seen_at
            WHERE visited_folders.folder IS NOT excluded.folder OR visited_folders.seen_at < $c
            """, ("$k", projectKey), ("$f", folder), ("$t", Iso(now)), ("$c", Iso(now - TimeSpan.FromHours(1))));

    /// <summary>The folders seen since the given time, most recent first, at most <see cref="FoldersMax"/>.</summary>
    public List<string> VisitedFolders(DateTimeOffset since)
    {
        var list = new List<string>();
        using var cmd = Command("SELECT folder FROM visited_folders WHERE seen_at >= $s ORDER BY seen_at DESC, rowid DESC LIMIT $n", null, ("$s", Iso(since)), ("$n", FoldersMax));
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>The folder a session of this project last worked in, or null when none did (or it was forgotten).</summary>
    public string? FolderOf(string projectKey) => Scalar("SELECT folder FROM visited_folders WHERE project_key = $k", ("$k", projectKey)) as string;

    public int ForgetFolders(DateTimeOffset before) => Exec("DELETE FROM visited_folders WHERE seen_at < $t", ("$t", Iso(before)));
}
