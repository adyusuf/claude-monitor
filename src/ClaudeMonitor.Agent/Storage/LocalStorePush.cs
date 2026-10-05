namespace ClaudeMonitor.Agent.Storage;

public sealed record PushedCommand(string Id, string Session, DateTimeOffset PushedAt);

/// <summary>
/// What the push into an idle session (ADR-0003) needs from the local database: which Claude Code process runs which
/// session, and the life of a pushed prompt (queued -> pushed -> taken once it shows in the transcript, or back to
/// queued when it never does).
/// </summary>
public sealed partial class LocalStore
{
    public const string PushedState = "pushed";

    // ---- session bindings ----------------------------------------------------------------------------------------

    /// <summary>A hook says: this session runs under that Claude Code process. The latest binding of a process is its session.</summary>
    public void BindSession(string session, int parentPid, DateTimeOffset now) =>
        Exec("""
            INSERT INTO session_bindings (session, parent_pid, seen_at) VALUES ($s, $p, $t)
            ON CONFLICT(session) DO UPDATE SET parent_pid = excluded.parent_pid, seen_at = excluded.seen_at
            """, ("$s", session), ("$p", parentPid), ("$t", Iso(now)));

    /// <summary>The session most recently seen under a Claude Code process (after /clear or /resume the id changes, the process does not).</summary>
    public string? SessionOf(int parentPid) =>
        Scalar("SELECT session FROM session_bindings WHERE parent_pid = $p ORDER BY seen_at DESC, rowid DESC LIMIT 1", ("$p", parentPid)) as string;

    // ---- the life of a pushed prompt -----------------------------------------------------------------------------

    /// <summary>
    /// Takes every unexpired queued prompt of a session (every session when null) for pushing, oldest first, and marks it
    /// pushed. A prompt is taken once: the hooks no longer see it.
    /// </summary>
    public List<LocalCommand> TakePromptsForPush(string? session, string kind, DateTimeOffset now)
    {
        using var tx = db.BeginTransaction(deferred: false);
        var taken = new List<LocalCommand>();
        using (var cmd = Command($"""
                   SELECT id, session, body, expires_at FROM commands
                   WHERE kind = $k AND state = 'queued'{(session is null ? "" : " AND session = $s")} ORDER BY rowid
                   """, tx, ("$k", kind), ("$s", session)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (At(r.GetString(3)) > now) taken.Add(new LocalCommand(r.GetString(0), r.GetString(1), kind, Str(r, 2), At(r.GetString(3))));
            }
        }

        taken.RemoveAll(c => Exec("UPDATE commands SET state = 'pushed' WHERE id = $i AND state = 'queued'", tx, ("$i", c.Id)) != 1);
        foreach (var c in taken)
        {
            Exec("INSERT OR REPLACE INTO pushes (command_id, pushed_at) VALUES ($i, $t)", tx, ("$i", c.Id), ("$t", Iso(now)));
        }

        tx.Commit();
        return taken;
    }

    /// <summary>A push could not be written: the prompt goes back to the queue untouched.</summary>
    public void PushFailed(string id)
    {
        Exec("UPDATE commands SET state = 'queued' WHERE id = $i AND state = 'pushed'", ("$i", id));
        Exec("DELETE FROM pushes WHERE command_id = $i AND confirmed_at IS NULL", ("$i", id));
    }

    public List<PushedCommand> UnconfirmedPushes()
    {
        var list = new List<PushedCommand>();
        using var cmd = Command("""
            SELECT c.id, c.session, p.pushed_at FROM pushes p JOIN commands c ON c.id = p.command_id
            WHERE p.confirmed_at IS NULL AND c.state = 'pushed' ORDER BY c.rowid
            """, null);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new PushedCommand(r.GetString(0), r.GetString(1), At(r.GetString(2))));
        return list;
    }

    /// <summary>The prompt reached the session (its id is in the transcript): it counts as applied (at the moment it was seen), and the web is told.</summary>
    public bool ConfirmPush(string id, DateTimeOffset now)
    {
        using var tx = db.BeginTransaction(deferred: false);
        var done = Exec("UPDATE commands SET state = 'taken', taken_at = $t WHERE id = $i AND state = 'pushed'", tx, ("$i", id), ("$t", Iso(now))) == 1;
        if (done) Exec("UPDATE pushes SET confirmed_at = $t WHERE command_id = $i", tx, ("$t", Iso(now)), ("$i", id));
        tx.Commit();
        return done;
    }

    /// <summary>
    /// Pushed prompts older than the wait that never showed up (the channel was not enabled, the session ended) go back to
    /// the queue, where the hooks deliver them as before. Returns how many.
    /// </summary>
    public int RequeueStalePushes(DateTimeOffset now, TimeSpan wait)
    {
        var stale = UnconfirmedPushes().Where(p => now - p.PushedAt >= wait).ToList();
        foreach (var p in stale) PushFailed(p.Id);
        return stale.Count;
    }

    public int PushedTotal() => (int)(long)(Scalar("SELECT COUNT(*) FROM pushes WHERE confirmed_at IS NOT NULL") ?? 0L);
}
