namespace Podium.Agent.Storage;

/// <summary>
/// One project's access policy as this machine holds it (ADR-0020 §4): the message the API delivered last (null once it stopped delivering it) and the
/// last one that verified with a pinned owner key (Kept), which a machine that requires signed approvals keeps until a newer signed version replaces it.
/// Messages are the API's JSON (<see cref="Podium.Contracts.AccessPolicyMessage"/>); they are judged again every time they are read.
/// </summary>
public sealed record StoredAccessPolicy(string ProjectId, string WorkspaceId, int? Version, string? Message, bool IsSigned, DateTimeOffset ReceivedAt,
    int? KeptVersion, string? KeptMessage);

/// <summary>The access policies delivered to this agent, and the stamp of the set they came with.</summary>
public sealed partial class LocalStore
{
    /// <summary>The stamp (with its workspace) of the policy set last fetched: a sync is needed only when the settings name another.</summary>
    public const string AccessSyncedKey = "access.stamp";

    private void CreateAccessTables() => Exec("""
        CREATE TABLE IF NOT EXISTS access_policies (project_id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL, version INTEGER, message TEXT,
            signed INTEGER NOT NULL DEFAULT 0, received_at TEXT NOT NULL, kept_version INTEGER, kept_message TEXT);
        """);

    /// <summary>Every stored policy, delivered or only kept.</summary>
    public List<StoredAccessPolicy> AccessPolicies()
    {
        using var cmd = Command("SELECT project_id, workspace_id, version, message, signed, received_at, kept_version, kept_message FROM access_policies ORDER BY project_id", null);
        using var r = cmd.ExecuteReader();
        var list = new List<StoredAccessPolicy>();
        while (r.Read())
        {
            list.Add(new StoredAccessPolicy(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4) == 1,
                Parse(r.GetString(5)), r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetString(7)));
        }

        return list;
    }

    /// <summary>
    /// Replaces the delivered set in one transaction, with the stamp it came with: each row's delivered message becomes the new one (null for a policy no
    /// longer delivered) and its kept message the one given; a row with neither left is deleted. Nothing is half-written if this fails.
    /// </summary>
    public void ReplaceAccessPolicies(IReadOnlyList<StoredAccessPolicy> rows, string syncedStamp)
    {
        ArgumentNullException.ThrowIfNull(rows);
        using var tx = db.BeginTransaction();
        Exec("UPDATE access_policies SET version = NULL, message = NULL, signed = 0", tx);
        foreach (var row in rows)
        {
            Exec("""
                INSERT INTO access_policies (project_id, workspace_id, version, message, signed, received_at, kept_version, kept_message)
                VALUES ($p, $w, $v, $m, $s, $t, $kv, $km)
                ON CONFLICT(project_id) DO UPDATE SET workspace_id = excluded.workspace_id, version = excluded.version, message = excluded.message,
                    signed = excluded.signed, received_at = excluded.received_at, kept_version = excluded.kept_version, kept_message = excluded.kept_message
                """, tx, ("$p", row.ProjectId), ("$w", row.WorkspaceId), ("$v", row.Version), ("$m", row.Message), ("$s", row.IsSigned ? 1 : 0),
                ("$t", Iso(row.ReceivedAt)), ("$kv", row.KeptVersion), ("$km", row.KeptMessage));
        }

        Exec("DELETE FROM access_policies WHERE message IS NULL AND kept_message IS NULL", tx);
        Exec("INSERT INTO kv (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", tx, ("$k", AccessSyncedKey), ("$v", syncedStamp));
        tx.Commit();
    }
}
