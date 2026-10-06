using System.Globalization;

namespace ClaudeMonitor.Agent.Storage;

/// <summary>A requester's call waiting for the daemon (MCP tools write it, the daemon sends it and writes the answer).</summary>
public sealed record RemoteRequest(string LocalId, string Kind, string Method, string Url, string? Body, string State, string? Result,
    string? Error, DateTimeOffset CreatedAt);

/// <summary>A run this agent asked for, followed until it finishes.</summary>
public sealed record FollowedRun(string RunId, string Status, string? View, int NextSeq, bool Done, DateTimeOffset UpdatedAt);

/// <summary>A run this agent was sent to execute, written before it starts so a replay never starts it twice.</summary>
public sealed record ExecRun(string RunId, string State, string? FinalStatus, int? ExitCode, string? Error, bool Truncated,
    string? ResolvedExe, bool Reported);

public sealed record StoredChunk(string RunId, int Seq, string Stream, string Body, bool Gap);

/// <summary>Remote work (ADR-0005) in the local database: requests for the API, runs followed, runs executed and their output.</summary>
public sealed partial class LocalStore
{
    public static class RequestStates
    {
        public const string New = "new";
        public const string Done = "done";
        public const string Failed = "failed";
    }

    public static class ExecStates
    {
        public const string Running = "running";
        public const string Finished = "finished";
    }

    private void CreateRemoteTables() => Exec("""
        CREATE TABLE IF NOT EXISTS remote_requests (local_id TEXT PRIMARY KEY, kind TEXT NOT NULL, method TEXT NOT NULL,
            url TEXT NOT NULL, body TEXT, state TEXT NOT NULL, result TEXT, error TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS followed_runs (run_id TEXT PRIMARY KEY, status TEXT NOT NULL, view TEXT, next_seq INTEGER NOT NULL,
            done INTEGER NOT NULL, dirty INTEGER NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS run_output (run_id TEXT NOT NULL, seq INTEGER NOT NULL, stream TEXT NOT NULL, body TEXT NOT NULL,
            gap INTEGER NOT NULL, PRIMARY KEY (run_id, seq));
        CREATE TABLE IF NOT EXISTS exec_runs (run_id TEXT PRIMARY KEY, state TEXT NOT NULL, final_status TEXT, exit_code INTEGER,
            error TEXT, truncated INTEGER NOT NULL DEFAULT 0, resolved_exe TEXT, started_at TEXT NOT NULL, reported INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS exec_output (run_id TEXT NOT NULL, seq INTEGER NOT NULL, stream TEXT NOT NULL, body TEXT NOT NULL,
            gap INTEGER NOT NULL, sent INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (run_id, seq));
        """);

    // ---- requests (requester side) ---------------------------------------------------------------------------------

    public void AddRequest(string localId, string kind, string method, string url, string? body, DateTimeOffset now) =>
        Exec("""
            INSERT INTO remote_requests (local_id, kind, method, url, body, state, created_at, updated_at)
            VALUES ($id, $k, $m, $u, $b, 'new', $t, $t) ON CONFLICT(local_id) DO NOTHING
            """, ("$id", localId), ("$k", kind), ("$m", method), ("$u", url), ("$b", body), ("$t", Iso(now)));

    public List<RemoteRequest> NewRequests(int limit) => Requests("WHERE state = 'new' ORDER BY created_at LIMIT $n", ("$n", limit));

    public RemoteRequest? Request(string localId) => Requests("WHERE local_id = $id", ("$id", localId)).FirstOrDefault();

    public void RequestAnswered(string localId, string state, string? result, string? error, DateTimeOffset now) =>
        Exec("UPDATE remote_requests SET state = $s, result = $r, error = $e, updated_at = $t WHERE local_id = $id",
            ("$id", localId), ("$s", state), ("$r", result), ("$e", error), ("$t", Iso(now)));

    /// <summary>Answered requests older than this are dropped; a run's outcome stays in followed_runs.</summary>
    public void ForgetRequests(DateTimeOffset before) =>
        Exec("DELETE FROM remote_requests WHERE state <> 'new' AND updated_at < $t", ("$t", Iso(before)));

    private List<RemoteRequest> Requests(string where, params (string Name, object? Value)[] args)
    {
        using var cmd = Command($"SELECT local_id, kind, method, url, body, state, result, error, created_at FROM remote_requests {where}", null, args);
        using var r = cmd.ExecuteReader();
        var list = new List<RemoteRequest>();
        while (r.Read())
        {
            list.Add(new RemoteRequest(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), Parse(r.GetString(8))));
        }

        return list;
    }

    /// <summary>
    /// Finished runs older than <paramref name="before"/> leave this machine's database: a followed run with its output, and
    /// an executed run once reported. What another machine printed is not kept here longer than the API keeps it.
    /// </summary>
    public void ForgetRuns(DateTimeOffset before)
    {
        Exec("DELETE FROM run_output WHERE run_id IN (SELECT run_id FROM followed_runs WHERE done = 1 AND updated_at < $t)", ("$t", Iso(before)));
        Exec("DELETE FROM followed_runs WHERE done = 1 AND updated_at < $t", ("$t", Iso(before)));
        Exec("DELETE FROM exec_runs WHERE state = 'finished' AND reported = 1 AND started_at < $t", ("$t", Iso(before)));
    }

    // ---- runs followed (requester side) ----------------------------------------------------------------------------

    public void FollowRun(string runId, string status, DateTimeOffset now) =>
        Exec("""
            INSERT INTO followed_runs (run_id, status, next_seq, done, dirty, updated_at) VALUES ($id, $s, -1, 0, 1, $t)
            ON CONFLICT(run_id) DO UPDATE SET dirty = 1
            """, ("$id", runId), ("$s", status), ("$t", Iso(now)));

    /// <summary>The stream said the run changed: fetch it at the next pass instead of waiting for the poll.</summary>
    public void RunChanged(string runId) => Exec("UPDATE followed_runs SET dirty = 1 WHERE run_id = $id", ("$id", runId));

    /// <summary>Unfinished runs that changed, or were last fetched before <paramref name="staleBefore"/>.</summary>
    public List<FollowedRun> RunsToFetch(DateTimeOffset staleBefore, int limit) =>
        Followed("WHERE done = 0 AND (dirty = 1 OR updated_at < $t) ORDER BY updated_at LIMIT $n", ("$t", Iso(staleBefore)), ("$n", limit));

    public FollowedRun? Followed(string runId) => Followed("WHERE run_id = $id", ("$id", runId)).FirstOrDefault();

    public void RunFetched(string runId, string status, string view, int nextSeq, bool done, DateTimeOffset now) =>
        Exec("UPDATE followed_runs SET status = $s, view = $v, next_seq = $n, done = $d, dirty = 0, updated_at = $t WHERE run_id = $id",
            ("$id", runId), ("$s", status), ("$v", view), ("$n", nextSeq), ("$d", done ? 1 : 0), ("$t", Iso(now)));

    public void AddRunOutput(string runId, IEnumerable<StoredChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        foreach (var c in chunks)
        {
            Exec("INSERT INTO run_output (run_id, seq, stream, body, gap) VALUES ($id, $q, $s, $b, $g) ON CONFLICT DO NOTHING",
                ("$id", runId), ("$q", c.Seq), ("$s", c.Stream), ("$b", c.Body), ("$g", c.Gap ? 1 : 0));
        }
    }

    public List<StoredChunk> RunOutput(string runId) => Chunks("run_output", "WHERE run_id = $id ORDER BY seq", ("$id", runId));

    private List<FollowedRun> Followed(string where, params (string Name, object? Value)[] args)
    {
        using var cmd = Command($"SELECT run_id, status, view, next_seq, done, updated_at FROM followed_runs {where}", null, args);
        using var r = cmd.ExecuteReader();
        var list = new List<FollowedRun>();
        while (r.Read())
        {
            list.Add(new FollowedRun(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt32(3), r.GetInt32(4) == 1,
                Parse(r.GetString(5))));
        }

        return list;
    }

    // ---- runs executed (target side) -------------------------------------------------------------------------------

    /// <summary>Records a run before it starts. False when it is already known: a replay, never started twice.</summary>
    public bool ExecBegin(string runId, DateTimeOffset now) =>
        Exec("INSERT INTO exec_runs (run_id, state, started_at) VALUES ($id, 'running', $t) ON CONFLICT(run_id) DO NOTHING",
            ("$id", runId), ("$t", Iso(now))) == 1;

    public void ExecEnd(string runId, string status, int? exitCode, string? error, bool truncated, string? resolvedExe) =>
        Exec("""
            UPDATE exec_runs SET state = 'finished', final_status = $s, exit_code = $x, error = $e, truncated = $tr,
                resolved_exe = COALESCE($r, resolved_exe), reported = 0 WHERE run_id = $id
            """, ("$id", runId), ("$s", status), ("$x", exitCode), ("$e", error), ("$tr", truncated ? 1 : 0), ("$r", resolvedExe));

    public ExecRun? ExecRunOf(string runId) => ExecRuns("WHERE run_id = $id", ("$id", runId)).FirstOrDefault();

    public List<ExecRun> ExecRunsIn(string state) => ExecRuns("WHERE state = $s", ("$s", state));

    /// <summary>Finished runs whose outcome the API has not acknowledged yet.</summary>
    public List<ExecRun> UnreportedExecs() => ExecRuns("WHERE state = 'finished' AND reported = 0");

    public void ExecReported(string runId) => Exec("UPDATE exec_runs SET reported = 1 WHERE run_id = $id", ("$id", runId));

    public void AddExecOutput(StoredChunk c)
    {
        ArgumentNullException.ThrowIfNull(c);
        Exec("INSERT INTO exec_output (run_id, seq, stream, body, gap) VALUES ($id, $q, $s, $b, $g) ON CONFLICT DO NOTHING",
            ("$id", c.RunId), ("$q", c.Seq), ("$s", c.Stream), ("$b", c.Body), ("$g", c.Gap ? 1 : 0));
    }

    public List<StoredChunk> UnsentExecOutput(int limit) =>
        Chunks("exec_output", "WHERE sent = 0 ORDER BY run_id, seq LIMIT $n", ("$n", limit));

    public bool HasUnsentOutput(string runId) =>
        Convert.ToInt64(Scalar("SELECT COUNT(*) FROM exec_output WHERE run_id = $id AND sent = 0", ("$id", runId)) ?? 0L,
            CultureInfo.InvariantCulture) > 0;

    /// <summary>Sent output is deleted at once: what a target printed stays on it only until the API has it.</summary>
    public void ExecOutputSent(string runId, int seq) =>
        Exec("DELETE FROM exec_output WHERE run_id = $id AND seq = $q", ("$id", runId), ("$q", seq));

    private List<ExecRun> ExecRuns(string where, params (string Name, object? Value)[] args)
    {
        using var cmd = Command($"SELECT run_id, state, final_status, exit_code, error, truncated, resolved_exe, reported FROM exec_runs {where}", null, args);
        using var r = cmd.ExecuteReader();
        var list = new List<ExecRun>();
        while (r.Read())
        {
            list.Add(new ExecRun(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5) == 1, r.IsDBNull(6) ? null : r.GetString(6), r.GetInt32(7) == 1));
        }

        return list;
    }

    private List<StoredChunk> Chunks(string table, string where, params (string Name, object? Value)[] args)
    {
        using var cmd = Command($"SELECT run_id, seq, stream, body, gap FROM {table} {where}", null, args);
        using var r = cmd.ExecuteReader();
        var list = new List<StoredChunk>();
        while (r.Read()) list.Add(new StoredChunk(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetInt32(4) == 1));
        return list;
    }

    private static DateTimeOffset Parse(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture);
}
