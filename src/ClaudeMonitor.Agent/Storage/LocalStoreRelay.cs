using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ClaudeMonitor.Agent.Storage;

public sealed record LocalCommand(string Id, string Session, string Kind, string? Body, DateTimeOffset ExpiresAt);

/// <summary>A command a hook took, and when (null for one taken before the agent recorded the time).</summary>
public sealed record TakenCommand(string Id, DateTimeOffset? At);
public sealed record PermissionAsk(string LocalId, string Harness, string Session, string ToolName, string ToolInput, int WaitSeconds,
    DateTimeOffset CreatedAt, string? RemoteId, string? Decision, string? Reason, string State);
public sealed record TranscriptCursor(string Session, string Harness, string Path, long Offset, string? ProjectKey, string? ProjectName,
    string? GitBranch);

public static class PermissionStates
{
    public const string New = "new";
    public const string Sent = "sent";
    public const string Answered = "answered";
    public const string Expired = "expired";
}

/// <summary>Commands, permission requests and transcript cursors: what the hooks and the daemon hand each other.</summary>
public sealed partial class LocalStore
{
    private static string Iso(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset At(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);

    // ---- commands from the web ----------------------------------------------------------------------------------

    public void SaveCommand(LocalCommand c)
    {
        ArgumentNullException.ThrowIfNull(c);
        Exec("INSERT OR IGNORE INTO commands (id, session, kind, body, expires_at, state) VALUES ($i, $s, $k, $b, $e, 'queued')",
            ("$i", c.Id), ("$s", c.Session), ("$k", c.Kind), ("$b", c.Body), ("$e", Iso(c.ExpiresAt)));
    }

    /// <summary>Takes (once) the oldest unexpired command of a kind for a session; a taken command is not taken again.</summary>
    public LocalCommand? TakeCommand(string session, string kind, DateTimeOffset now)
    {
        using var tx = db.BeginTransaction();
        LocalCommand? found = null;
        using (var cmd = Command("SELECT id, body, expires_at FROM commands WHERE session = $s AND kind = $k AND state = 'queued' ORDER BY rowid",
                   tx, ("$s", session), ("$k", kind)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read() && found is null)
            {
                var expires = At(r.GetString(2));
                if (expires > now) found = new LocalCommand(r.GetString(0), session, kind, Str(r, 1), expires);
            }
        }

        if (found is not null) Exec("UPDATE commands SET state = 'taken', taken_at = $t WHERE id = $i", tx, ("$i", found.Id), ("$t", Iso(now)));
        tx.Commit();
        return found;
    }

    /// <summary>Commands still waiting for a hook to take them (not expired), and when the soonest of them expires.</summary>
    public (int Count, DateTimeOffset? Soonest) WaitingCommands(DateTimeOffset now)
    {
        var waiting = new List<DateTimeOffset>();
        using var cmd = Command("SELECT expires_at FROM commands WHERE state = 'queued'", null);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (At(r.GetString(0)) is var expires && expires > now) waiting.Add(expires);
        }

        return (waiting.Count, waiting.Count == 0 ? null : waiting.Min());
    }

    /// <summary>Taken commands whose outcome the daemon has not reported yet.</summary>
    public List<TakenCommand> TakenCommands()
    {
        var taken = new List<TakenCommand>();
        using var cmd = Command("SELECT id, taken_at FROM commands WHERE state = 'taken'", null);
        using var r = cmd.ExecuteReader();
        while (r.Read()) taken.Add(new TakenCommand(r.GetString(0), Str(r, 1) is { } at ? At(at) : null));
        return taken;
    }

    public void MarkCommandReported(string id) => Exec("UPDATE commands SET state = 'reported' WHERE id = $i", ("$i", id));

    // ---- permission requests ------------------------------------------------------------------------------------

    public void AddPermission(PermissionAsk p)
    {
        ArgumentNullException.ThrowIfNull(p);
        Exec("""
            INSERT INTO permissions (local_id, harness, session, tool_name, tool_input, wait_seconds, created_at, state)
            VALUES ($l, $h, $s, $t, $in, $w, $c, 'new')
            """, ("$l", p.LocalId), ("$h", p.Harness), ("$s", p.Session), ("$t", p.ToolName), ("$in", p.ToolInput),
            ("$w", p.WaitSeconds), ("$c", Iso(p.CreatedAt)));
    }

    public PermissionAsk? Permission(string localId) => Permissions("local_id = $x", ("$x", localId)).FirstOrDefault();

    public List<PermissionAsk> PermissionsIn(string state) => Permissions("state = $x", ("$x", state));

    public void PermissionSent(string localId, string remoteId) =>
        Exec("UPDATE permissions SET remote_id = $r, state = 'sent' WHERE local_id = $l AND state = 'new'", ("$r", remoteId), ("$l", localId));

    public void PermissionAnswered(string remoteId, string decision, string? reason) =>
        Exec("UPDATE permissions SET decision = $d, reason = $why, state = 'answered' WHERE remote_id = $r AND state = 'sent'",
            ("$d", decision), ("$why", reason), ("$r", remoteId));

    public void PermissionExpired(string localId) =>
        Exec("UPDATE permissions SET state = 'expired' WHERE local_id = $l AND state IN ('new', 'sent')", ("$l", localId));

    private List<PermissionAsk> Permissions(string where, params (string, object?)[] args)
    {
        var list = new List<PermissionAsk>();
        using var cmd = Command($"""
            SELECT local_id, harness, session, tool_name, tool_input, wait_seconds, created_at, remote_id, decision, reason, state
            FROM permissions WHERE {where} ORDER BY created_at
            """, null, args);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PermissionAsk(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5),
                At(r.GetString(6)), Str(r, 7), Str(r, 8), Str(r, 9), r.GetString(10)));
        }

        return list;
    }

    // ---- transcripts --------------------------------------------------------------------------------------------

    /// <summary>Remembers a session's transcript file; the offset already read is kept when the row exists.</summary>
    public void TrackTranscript(TranscriptCursor t, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(t);
        Exec("""
            INSERT INTO transcripts (session, harness, path, offset, project_key, project_name, git_branch, updated_at)
            VALUES ($s, $h, $p, 0, $pk, $pn, $b, $u)
            ON CONFLICT(session) DO UPDATE SET path = excluded.path, project_key = excluded.project_key,
                project_name = excluded.project_name, git_branch = excluded.git_branch, updated_at = excluded.updated_at
            """, ("$s", t.Session), ("$h", t.Harness), ("$p", t.Path), ("$pk", t.ProjectKey), ("$pn", t.ProjectName),
            ("$b", t.GitBranch), ("$u", Iso(now)));
    }

    public List<TranscriptCursor> Transcripts(DateTimeOffset activeSince)
    {
        var list = new List<TranscriptCursor>();
        using var cmd = Command("""
            SELECT session, harness, path, offset, project_key, project_name, git_branch FROM transcripts WHERE updated_at >= $since
            """, null, ("$since", Iso(activeSince)));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new TranscriptCursor(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), Str(r, 4), Str(r, 5), Str(r, 6)));
        }

        return list;
    }

    public void TranscriptRead(string session, long offset) =>
        Exec("UPDATE transcripts SET offset = $o WHERE session = $s", ("$o", offset), ("$s", session));

    /// <summary>True the first time a message id is seen for a session (usage is counted once per message).</summary>
    public bool FirstSight(string session, string messageId) =>
        Exec("INSERT OR IGNORE INTO usage_seen (session, message_id) VALUES ($s, $m)", ("$s", session), ("$m", messageId)) == 1;

    internal SqliteConnection Connection => db;
}
