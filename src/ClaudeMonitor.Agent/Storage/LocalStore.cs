using ClaudeMonitor.Contracts;
using Microsoft.Data.Sqlite;

namespace ClaudeMonitor.Agent.Storage;

/// <summary>
/// The agent's local database (SQLite in the user-only home directory). It is the ONLY channel between the
/// short-lived hook processes and the daemon: hooks write events and permission requests and read commands; the
/// daemon uploads, relays and fills in answers. Nothing listens on a port, and an event written while the daemon
/// is down waits here until it starts (ADR-0002).
/// </summary>
public sealed partial class LocalStore : IDisposable
{
    private readonly SqliteConnection db;

    public LocalStore(string path)
    {
        db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 10,
        }.ToString());
        db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS outbox (id INTEGER PRIMARY KEY AUTOINCREMENT, harness TEXT NOT NULL,
                session TEXT NOT NULL, kind TEXT NOT NULL, occurred_at TEXT NOT NULL, payload TEXT NOT NULL,
                truncated INTEGER NOT NULL, project_key TEXT, project_name TEXT, git_branch TEXT, batch INTEGER);
            CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS commands (id TEXT PRIMARY KEY, session TEXT NOT NULL, kind TEXT NOT NULL,
                body TEXT, expires_at TEXT NOT NULL, state TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS permissions (local_id TEXT PRIMARY KEY, harness TEXT NOT NULL, session TEXT NOT NULL,
                tool_name TEXT NOT NULL, tool_input TEXT NOT NULL, wait_seconds INTEGER NOT NULL, created_at TEXT NOT NULL,
                remote_id TEXT, decision TEXT, reason TEXT, state TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS transcripts (session TEXT PRIMARY KEY, harness TEXT NOT NULL, path TEXT NOT NULL,
                offset INTEGER NOT NULL, project_key TEXT, project_name TEXT, git_branch TEXT, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS usage_seen (session TEXT NOT NULL, message_id TEXT NOT NULL,
                PRIMARY KEY (session, message_id));
            """);
    }

    public void Dispose() => db.Dispose();

    // ---- key/value -------------------------------------------------------------------------------------------

    public string? Get(string key) =>
        Scalar("SELECT value FROM kv WHERE key = $k", ("$k", key)) as string;

    public void Set(string key, string value) =>
        Exec("INSERT INTO kv (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$k", key), ("$v", value));

    // ---- outbox -----------------------------------------------------------------------------------------------

    public void Enqueue(CapturedEvent e, string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(e);
        Exec("""
            INSERT INTO outbox (harness, session, kind, occurred_at, payload, truncated, project_key, project_name, git_branch)
            VALUES ($h, $s, $k, $o, $p, $t, $pk, $pn, $b)
            """, ("$h", e.HarnessKind), ("$s", e.SessionExternalId), ("$k", e.Kind), ("$o", e.OccurredAt.ToString("O")),
            ("$p", payloadJson), ("$t", e.Truncated ? 1 : 0), ("$pk", e.ProjectKey), ("$pn", e.ProjectName), ("$b", e.GitBranch));
    }

    public long OutboxCount() => (long)(Scalar("SELECT COUNT(*) FROM outbox") ?? 0L);

    /// <summary>
    /// The next batch to send. A batch already numbered (sent before, not acknowledged) is resent as it was, with the
    /// same number and the same rows, so the API can recognise the retry. Otherwise the oldest rows, up to the
    /// limits, get the next number.
    /// </summary>
    public (long Seq, List<(long Id, CapturedEvent Event)> Rows)? NextBatch(int maxEvents, int maxBytes)
    {
        using var tx = db.BeginTransaction();
        var pending = Scalar("SELECT MIN(batch) FROM outbox WHERE batch IS NOT NULL", tx: tx);
        long seq;
        if (pending is long existing)
        {
            seq = existing;
        }
        else
        {
            var ids = new List<long>();
            long bytes = 0;
            using (var cmd = Command("SELECT id, length(payload) FROM outbox ORDER BY id LIMIT $n", tx, ("$n", maxEvents)))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    bytes += r.GetInt64(1);
                    if (ids.Count > 0 && bytes > maxBytes) break;
                    ids.Add(r.GetInt64(0));
                }
            }

            if (ids.Count == 0) return null;
            seq = long.Parse(Scalar("SELECT value FROM kv WHERE key = 'batch_seq'", tx: tx) as string ?? "0",
                System.Globalization.CultureInfo.InvariantCulture) + 1;
            Exec("INSERT INTO kv (key, value) VALUES ('batch_seq', $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                tx, ("$v", seq.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Exec($"UPDATE outbox SET batch = $b WHERE id IN ({string.Join(',', ids)})", tx, ("$b", seq));
        }

        var rows = new List<(long, CapturedEvent)>();
        using (var cmd = Command("""
                   SELECT id, harness, session, kind, occurred_at, payload, truncated, project_key, project_name, git_branch
                   FROM outbox WHERE batch = $b ORDER BY id
                   """, tx, ("$b", seq)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                using var payload = System.Text.Json.JsonDocument.Parse(r.GetString(5));
                rows.Add((r.GetInt64(0), new CapturedEvent(r.GetString(1), r.GetString(2), r.GetString(3),
                    DateTimeOffset.Parse(r.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
                    payload.RootElement.Clone(), r.GetInt64(6) == 1, Str(r, 7), Str(r, 8), Str(r, 9))));
            }
        }

        tx.Commit();
        return (seq, rows);
    }

    public void Acknowledge(long seq) => Exec("DELETE FROM outbox WHERE batch = $b", ("$b", seq));

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    // ---- plumbing ---------------------------------------------------------------------------------------------

    private SqliteCommand Command(string sql, SqliteTransaction? tx, params (string Name, object? Value)[] args)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private int Exec(string sql, params (string Name, object? Value)[] args) => Exec(sql, null, args);

    private int Exec(string sql, SqliteTransaction? tx, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, tx, args);
        return cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] args) => Scalar(sql, null, args);

    private object? Scalar(string sql, SqliteTransaction? tx, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, tx, args);
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }
}
