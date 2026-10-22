namespace Podium.Agent.Storage;

/// <summary>Where a session's inbox socket is and the key to write to it. The token is a secret: this row never leaves the user-only database.</summary>
public sealed record SessionInbox(string Session, string Endpoint, string? Token, DateTimeOffset SeenAt, string? PermissionMode = null, string? Tools = null);

/// <summary>
/// The session inboxes (ADR-0007): the hooks of a session store the socket Claude Code exported to them, the daemon reads it to write
/// a prompt from the web, and the row goes when the session ends or when the socket stops answering.
/// </summary>
public sealed partial class LocalStore
{
    public const string InboxErrorKey = "inbox.last_error";
    public const string InboxErrorAtKey = "inbox.last_error_at";

    /// <summary>
    /// A hook says: this session listens on that socket. The latest hook of a session wins (a resumed session binds a new socket). A hook that
    /// says what is already known writes nothing, except once an hour to show the session is alive: this runs on every tool call.
    /// </summary>
    public void SaveInbox(string session, string endpoint, string? token, DateTimeOffset now, string? permissionMode = null, string? tools = null) =>
        Exec("""
            INSERT INTO session_inboxes (session, endpoint, token, seen_at, permission_mode, tools) VALUES ($s, $e, $k, $t, $m, $n)
            ON CONFLICT(session) DO UPDATE SET endpoint = excluded.endpoint, token = excluded.token, seen_at = excluded.seen_at,
                permission_mode = coalesce(excluded.permission_mode, session_inboxes.permission_mode),
                tools = coalesce(excluded.tools, session_inboxes.tools)
            WHERE session_inboxes.endpoint IS NOT excluded.endpoint OR session_inboxes.token IS NOT excluded.token OR session_inboxes.seen_at < $c
                OR (excluded.permission_mode IS NOT NULL AND session_inboxes.permission_mode IS NOT excluded.permission_mode)
                OR (excluded.tools IS NOT NULL AND session_inboxes.tools IS NOT excluded.tools)
            """, ("$s", session), ("$e", endpoint), ("$k", token), ("$t", Iso(now)), ("$c", Iso(now - TimeSpan.FromHours(1))), ("$m", permissionMode), ("$n", tools));

    public SessionInbox? InboxOf(string session)
    {
        using var cmd = Command("SELECT session, endpoint, token, seen_at, permission_mode, tools FROM session_inboxes WHERE session = $s", null, ("$s", session));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new SessionInbox(r.GetString(0), r.GetString(1), Str(r, 2), At(r.GetString(3)), Str(r, 4), Str(r, 5)) : null;
    }

    /// <summary>Every session with a known socket, most recently seen first.</summary>
    public List<SessionInbox> Inboxes()
    {
        var list = new List<SessionInbox>();
        using var cmd = Command("SELECT session, endpoint, token, seen_at, permission_mode, tools FROM session_inboxes ORDER BY seen_at DESC, rowid DESC", null);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new SessionInbox(r.GetString(0), r.GetString(1), Str(r, 2), At(r.GetString(3)), Str(r, 4), Str(r, 5)));
        return list;
    }

    /// <summary>The session ended, or its socket did not answer: it is not written to again until a hook says where it listens.</summary>
    public void DropInbox(string session) => Exec("DELETE FROM session_inboxes WHERE session = $s", ("$s", session));

    /// <summary>Inboxes no hook has refreshed for that long belong to sessions that ended without a SessionEnd (a crash).</summary>
    public int PruneInboxes(DateTimeOffset olderThan) => Exec("DELETE FROM session_inboxes WHERE seen_at < $t", ("$t", Iso(olderThan)));
}
