namespace Podium.Agent.Storage;

/// <summary>
/// Events the API refuses for good (a poison event would otherwise be sent, refused and sent again for ever, with every later event waiting behind it).
/// They are moved aside, counted, and deleted after the retention like any captured content.
/// </summary>
public sealed partial class LocalStore
{
    /// <summary>How many events were moved aside since this database was made (the tray and `aipod status` can show it; ids and counts only).</summary>
    public const string QuarantinedKey = "relay.quarantined";

    private void CreateQuarantineTable() => Exec("""
        CREATE TABLE IF NOT EXISTS outbox_quarantine (id INTEGER PRIMARY KEY AUTOINCREMENT, harness TEXT NOT NULL,
            session TEXT NOT NULL, kind TEXT NOT NULL, occurred_at TEXT NOT NULL, payload TEXT NOT NULL,
            truncated INTEGER NOT NULL, project_key TEXT, project_name TEXT, git_branch TEXT, status INTEGER NOT NULL,
            quarantined_at TEXT NOT NULL);
        """);

    /// <summary>Moves the rows of a numbered batch the API refused for good out of the outbox, in one step; returns how many, and counts them.</summary>
    public int Quarantine(long seq, int status, DateTimeOffset now)
    {
        using var tx = db.BeginTransaction();
        var moved = Exec("""
            INSERT INTO outbox_quarantine (harness, session, kind, occurred_at, payload, truncated, project_key, project_name, git_branch, status, quarantined_at)
            SELECT harness, session, kind, occurred_at, payload, truncated, project_key, project_name, git_branch, $s, $t FROM outbox WHERE batch = $b ORDER BY id
            """, tx, ("$s", status), ("$t", Iso(now)), ("$b", seq));
        Exec("DELETE FROM outbox WHERE batch = $b", tx, ("$b", seq));
        if (moved > 0)
        {
            Exec("INSERT INTO kv (key, value) VALUES ($k, $n) ON CONFLICT(key) DO UPDATE SET value = CAST(CAST(value AS INTEGER) + $n AS TEXT)",
                tx, ("$k", QuarantinedKey), ("$n", moved));
        }

        tx.Commit();
        return moved;
    }

    /// <summary>The events held aside.</summary>
    public long QuarantineCount() => (long)(Scalar("SELECT COUNT(*) FROM outbox_quarantine") ?? 0L);

    /// <summary>The kinds of the events held aside, oldest first (never their content): what a test or a status line may show.</summary>
    public List<string> QuarantinedKinds()
    {
        var kinds = new List<string>();
        using var cmd = Command("SELECT kind FROM outbox_quarantine ORDER BY id", null);
        using var r = cmd.ExecuteReader();
        while (r.Read()) kinds.Add(r.GetString(0));
        return kinds;
    }

    /// <summary>Deletes what was moved aside before the retention (it is captured content); returns how many.</summary>
    public int ForgetQuarantined(DateTimeOffset before) => Exec("DELETE FROM outbox_quarantine WHERE quarantined_at < $t", ("$t", Iso(before)));
}
