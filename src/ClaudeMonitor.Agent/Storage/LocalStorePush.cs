using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Storage;

public sealed record PushedCommand(string Id, string Session, DateTimeOffset PushedAt);

/// <summary>A push the web has not been told about yet: the command, when it was written into the session, and through which transport.</summary>
public sealed record PushToReport(string Id, DateTimeOffset PushedAt, string Transport);

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
    /// pushed through <paramref name="transport"/> (the channel by default; the inbox pump says so). A prompt is taken once: the hooks no longer see it.
    /// </summary>
    public List<LocalCommand> TakePromptsForPush(string? session, string kind, DateTimeOffset now, string transport = CommandTransports.Channel, string? format = null)
    {
        using var tx = db.BeginTransaction(deferred: false);
        var taken = new List<LocalCommand>();
        using (var cmd = Command($"""
                   SELECT id, session, body, expires_at FROM commands
                   WHERE kind = $k AND state = 'queued' AND push_tried IS NULL{(session is null ? "" : " AND session = $s")} ORDER BY rowid
                   """, tx, ("$k", kind), ("$s", session)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (At(r.GetString(3)) > now) taken.Add(new LocalCommand(r.GetString(0), r.GetString(1), kind, Str(r, 2), At(r.GetString(3))));
            }
        }

        taken.RemoveAll(c => Exec("UPDATE commands SET state = 'pushed', transport = $x, format = $f WHERE id = $i AND state = 'queued'", tx, ("$i", c.Id), ("$x", transport), ("$f", format)) != 1);
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
        // The web was told it was pushed: it is told that it was not, once, so it does not wait for a message that will never show.
        Exec("INSERT OR IGNORE INTO push_retractions (command_id) SELECT command_id FROM pushes WHERE command_id = $i AND confirmed_at IS NULL AND reported = 1", ("$i", id));
        Exec("UPDATE commands SET state = 'queued', transport = NULL WHERE id = $i AND state = 'pushed'", ("$i", id));
        Exec("DELETE FROM pushes WHERE command_id = $i AND confirmed_at IS NULL", ("$i", id));
    }

    /// <summary>Pushes written into a session that the web has not heard about, oldest first.</summary>
    public List<PushToReport> PushesToReport()
    {
        var list = new List<PushToReport>();
        using var cmd = Command("""
            SELECT c.id, p.pushed_at, c.transport FROM pushes p JOIN commands c ON c.id = p.command_id
            WHERE p.confirmed_at IS NULL AND c.state = 'pushed' AND p.reported IS NULL AND c.transport IS NOT NULL ORDER BY c.rowid
            """, null);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new PushToReport(r.GetString(0), At(r.GetString(1)), r.GetString(2)));
        return list;
    }

    public void MarkPushReported(string id) => Exec("UPDATE pushes SET reported = 1 WHERE command_id = $i", ("$i", id));

    /// <summary>Commands whose push the web was told about and that went back to the hooks since.</summary>
    public List<string> PushRetractions()
    {
        var list = new List<string>();
        using var cmd = Command("SELECT command_id FROM push_retractions ORDER BY rowid", null);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public void ClearPushRetraction(string id) => Exec("DELETE FROM push_retractions WHERE command_id = $i", ("$i", id));

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
        foreach (var p in stale)
        {
            // It reached the session's door and was never seen: pushing it again would put another copy there (or another dialog) every wait.
            Exec("UPDATE commands SET push_tried = 1 WHERE id = $i", ("$i", p.Id));
            PushFailed(p.Id);
        }

        return stale.Count;
    }

    /// <summary>
    /// A prompt pushed to this session, for monitor_inbox_read (ADR-0007 A2): only a prompt that was pushed to exactly this session and still has its text.
    /// Null for an unknown id, another session's id, a prompt still waiting for a hook, or one whose text was forgotten: the caller cannot tell which.
    /// </summary>
    public LocalCommand? ReadablePush(string session, string id)
    {
        using var cmd = Command("""
            SELECT body, expires_at FROM commands
            WHERE id = $i AND session = $s AND kind = 'prompt' AND state IN ('pushed', 'taken', 'reported') AND body IS NOT NULL
            """, null, ("$i", id), ("$s", session));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new LocalCommand(id, session, "prompt", Str(r, 0), At(r.GetString(1))) : null;
    }

    /// <summary>The session read the prompt: the first time is kept, and every read is counted.</summary>
    public void MarkRead(string id, DateTimeOffset now) =>
        Exec("UPDATE commands SET reads = COALESCE(reads, 0) + 1, read_at = COALESCE(read_at, $t) WHERE id = $i", ("$t", Iso(now)), ("$i", id));

    /// <summary>Of the prompts that went to a session as a one-line notice: how many it read and how many it has not read (yet).</summary>
    public (int Read, int Unread) ShortNotices()
    {
        var read = (int)(long)(Scalar("SELECT COUNT(*) FROM commands WHERE format = 'short' AND read_at IS NOT NULL") ?? 0L);
        var unread = (int)(long)(Scalar("SELECT COUNT(*) FROM commands WHERE format = 'short' AND read_at IS NULL AND state IN ('pushed', 'taken', 'reported')") ?? 0L);
        return (read, unread);
    }

    /// <summary>The text of a prompt that was applied and is older than the retention is forgotten (the row stays: its id is what stops a prompt being taken twice). Returns how many.</summary>
    public int ForgetCommandBodies(DateTimeOffset before) =>
        Exec("UPDATE commands SET body = NULL WHERE body IS NOT NULL AND state IN ('taken', 'reported') AND taken_at IS NOT NULL AND taken_at < $t", ("$t", Iso(before)));

    public int PushedTotal() => (int)(long)(Scalar("SELECT COUNT(*) FROM pushes WHERE confirmed_at IS NOT NULL") ?? 0L);
}
