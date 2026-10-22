namespace Podium.Agent.Storage;

/// <summary>An ask this machine sent, as the daemon last read it from the API: its status and the whole view (the answer included), and whether it is over.</summary>
public sealed record FollowedAsk(string AskId, string Status, string? View, bool Done, DateTimeOffset UpdatedAt);

/// <summary>
/// The asks this machine sent (ADR-0008 §3f, the requester's side; `asks_out`): the daemon follows each one until it ends, on the API's ask_update at once and
/// otherwise every <c>RemotePollEvery</c>, and keeps what it read here, so `aipod_ask_result` and `aipod result` read the local database like they do for
/// runs and never call the API. The answer is untrusted text another Claude wrote: it stays in this user-only database and is deleted with the ask after the
/// local retention.
/// </summary>
public sealed partial class LocalStore
{
    private void CreateAsksOutTable() => Exec("""
        CREATE TABLE IF NOT EXISTS asks_out (ask_id TEXT PRIMARY KEY, status TEXT NOT NULL, view TEXT, done INTEGER NOT NULL, dirty INTEGER NOT NULL,
            updated_at TEXT NOT NULL);
        """);

    /// <summary>The API created the ask: follow it (fetch it at the next pass).</summary>
    public void FollowAsk(string askId, string status, DateTimeOffset now) =>
        Exec("""
            INSERT INTO asks_out (ask_id, status, done, dirty, updated_at) VALUES ($id, $s, 0, 1, $t)
            ON CONFLICT(ask_id) DO UPDATE SET dirty = 1
            """, ("$id", askId), ("$s", status), ("$t", Iso(now)));

    /// <summary>The stream said the ask changed: fetch it at the next pass instead of waiting for the poll.</summary>
    public void AskChanged(string askId) => Exec("UPDATE asks_out SET dirty = 1 WHERE ask_id = $id", ("$id", askId));

    /// <summary>Unfinished asks that changed, or were last fetched before <paramref name="staleBefore"/>.</summary>
    public List<FollowedAsk> AsksToFetch(DateTimeOffset staleBefore, int limit) =>
        FollowedAsks("WHERE done = 0 AND (dirty = 1 OR updated_at < $t) ORDER BY updated_at LIMIT $n", ("$t", Iso(staleBefore)), ("$n", limit));

    public FollowedAsk? AskOut(string askId) => FollowedAsks("WHERE ask_id = $id", ("$id", askId)).FirstOrDefault();

    public void AskFetched(string askId, string status, string view, bool done, DateTimeOffset now) =>
        Exec("UPDATE asks_out SET status = $s, view = $v, done = $d, dirty = 0, updated_at = $t WHERE ask_id = $id",
            ("$id", askId), ("$s", status), ("$v", view), ("$d", done ? 1 : 0), ("$t", Iso(now)));

    /// <summary>Finished asks older than this are dropped, with their answers.</summary>
    public int ForgetAsksOut(DateTimeOffset before) => Exec("DELETE FROM asks_out WHERE done = 1 AND updated_at < $t", ("$t", Iso(before)));

    private List<FollowedAsk> FollowedAsks(string where, params (string Name, object? Value)[] args)
    {
        using var cmd = Command($"SELECT ask_id, status, view, done, updated_at FROM asks_out {where}", null, args);
        using var r = cmd.ExecuteReader();
        var list = new List<FollowedAsk>();
        while (r.Read()) list.Add(new FollowedAsk(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt32(3) == 1, Parse(r.GetString(4))));
        return list;
    }
}
