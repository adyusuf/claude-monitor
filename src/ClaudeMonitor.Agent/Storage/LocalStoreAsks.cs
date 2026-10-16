using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Storage;

/// <summary>An ask this machine received (ADR-0008 §3f): the text, who sent it, and where it stands here.</summary>
public sealed record AskIn(string Id, string FromLabel, string Question, string? Project, DateTimeOffset NotAfter, string State, string? Session,
    DateTimeOffset? DeliveredAt);

/// <summary>
/// The asks the daemon received from another machine and carries to an idle session. The question is text another machine's Claude wrote: it stays in
/// this user-only database, is never logged and is deleted once the ask ends. The states are local: queued (received), delivered (written to a
/// session's inbox), confirmed (it showed in the session's transcript) and closed.
/// </summary>
public sealed partial class LocalStore
{
    public const string AskQueued = "queued";
    public const string AskDelivered = "delivered";
    public const string AskConfirmed = "confirmed";
    public const string AskClosed = "closed";

    private void CreateAskTables() => Exec("""
        CREATE TABLE IF NOT EXISTS asks_in (id TEXT PRIMARY KEY, from_label TEXT NOT NULL, question TEXT NOT NULL, project TEXT, not_after TEXT NOT NULL,
            state TEXT NOT NULL, session TEXT, delivered_at TEXT, confirmed_at TEXT, created_at TEXT NOT NULL);
        """);

    /// <summary>A received ask; one already known is left as it is (the API replays the queued ones on every connection).</summary>
    public bool SaveAskIn(AskMessage ask, DateTimeOffset now) =>
        Exec("""
            INSERT OR IGNORE INTO asks_in (id, from_label, question, project, not_after, state, created_at)
            VALUES ($i, $f, $q, $p, $n, 'queued', $t)
            """, ("$i", ask.Id.ToString()), ("$f", ask.FromLabel), ("$q", ask.Question), ("$p", ask.ProjectKey), ("$n", Iso(ask.NotAfter)), ("$t", Iso(now))) == 1;

    public List<AskIn> QueuedAsks(DateTimeOffset now) => Asks("WHERE state = 'queued' AND not_after > $n ORDER BY created_at", ("$n", Iso(now)));

    /// <summary>Queued asks past their time: nobody took them, the API expires them itself.</summary>
    public List<AskIn> StaleQueuedAsks(DateTimeOffset now) => Asks("WHERE state = 'queued' AND not_after <= $n", ("$n", Iso(now)));

    public List<AskIn> UnconfirmedAsks() => Asks("WHERE state = 'delivered' ORDER BY created_at");

    public AskIn? AskIn(string id) => Asks("WHERE id = $i", ("$i", id)).FirstOrDefault();

    /// <summary>The ask written to this session that has not been answered or closed, if any: the only one it may answer.</summary>
    public AskIn? OpenAskFor(string session, string id) =>
        Asks("WHERE id = $i AND session = $s AND state IN ('delivered', 'confirmed')", ("$i", id), ("$s", session)).FirstOrDefault();

    public bool AskDeliveredTo(string id, string session, DateTimeOffset now) =>
        Exec("UPDATE asks_in SET state = 'delivered', session = $s, delivered_at = $t WHERE id = $i AND state = 'queued'", ("$i", id), ("$s", session), ("$t", Iso(now))) == 1;

    public bool ConfirmAsk(string id, DateTimeOffset now) =>
        Exec("UPDATE asks_in SET state = 'confirmed', confirmed_at = $t WHERE id = $i AND state = 'delivered'", ("$i", id), ("$t", Iso(now))) == 1;

    /// <summary>The ask ended here (answered, not taken, cancelled): its text is deleted and only the id stays for a while, so a replay does not deliver it again.</summary>
    public void CloseAsk(string id) => Exec("UPDATE asks_in SET state = 'closed', question = '' WHERE id = $i", ("$i", id));

    public int ForgetAsks(DateTimeOffset before) => Exec("DELETE FROM asks_in WHERE state = 'closed' AND created_at < $t", ("$t", Iso(before)));

    /// <summary>The project key of a session's working folder, as its hooks recorded it.</summary>
    public string? ProjectKeyOf(string session) => Scalar("SELECT project_key FROM transcripts WHERE session = $s", ("$s", session)) as string;

    /// <summary>The Claude Code process id a session runs under (the hooks bound it).</summary>
    public int? PidOfSession(string session) => Scalar("SELECT parent_pid FROM session_bindings WHERE session = $s", ("$s", session)) is long pid ? (int)pid : null;

    private List<AskIn> Asks(string where, params (string, object?)[] args)
    {
        var list = new List<AskIn>();
        using var cmd = Command($"SELECT id, from_label, question, project, not_after, state, session, delivered_at FROM asks_in {where}", null, args);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AskIn(r.GetString(0), r.GetString(1), r.GetString(2), Str(r, 3), At(r.GetString(4)), r.GetString(5), Str(r, 6), Str(r, 7) is { } d ? At(d) : null));
        }

        return list;
    }
}
