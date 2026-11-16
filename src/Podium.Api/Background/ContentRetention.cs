using System.Text.Json;
using Podium.Api.Data;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Background;

/// <summary>
/// Retention of the captured content the event archiver does not cover (F-119): past the workspace's <c>retention_days</c> a web prompt's text
/// (<c>session_commands.body</c>) is set to NULL and a permission request's tool input (<c>permission_requests.tool_input</c>, NOT NULL) to
/// <c>{}</c>, the convention account deletion uses. The rows stay (ids, status, times and the audit still make sense). A command still queued
/// or delivered and an open request are never touched. The archive files the archiver wrote get the same lifetime once more: a file whose
/// <c>event_archives</c> row is older than the retention is deleted with its row, oldest first and only under the archive folder. Each step does at most one batch per pass, every change is
/// a guarded update (a row already blank, or no longer final, is not counted), so a pass can run any number of times.
/// </summary>
public static class ContentRetention
{
    public const int Batch = 1000;

    // Arrays, not sets: EF Core translates Contains on an array (IN).
    private static readonly string[] CommandPending = [CommandStatuses.Queued, CommandStatuses.Delivered];

    /// <summary>The blank tool input (account deletion writes the same).</summary>
    public const string BlankInput = "{}";

    /// <summary>One pass of the three steps; returns how many rows and files it changed.</summary>
    public static async Task<int> RunOnceAsync(PodiumDb db, string archiveDir, ILogger log, DateTimeOffset now, CancellationToken ct, int batch = Batch)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveDir);
        ArgumentNullException.ThrowIfNull(log);
        var changed = 0;
        await Chores.RunAsync(db, ct,
            async () => changed += await CommandBodiesAsync(db, now, batch, ct),
            async () => changed += await PermissionInputsAsync(db, now, batch, ct),
            async () => changed += await ArchiveFilesAsync(db, archiveDir, log, now, batch, ct));
        return changed;
    }

    private static async Task<int> CommandBodiesAsync(PodiumDb db, DateTimeOffset now, int batch, CancellationToken ct)
    {
        var due = await (from c in db.SessionCommands.AsNoTracking()
                         join s in db.WorkspaceSettings.AsNoTracking() on c.WorkspaceId equals s.WorkspaceId
                         where c.Body != null && !CommandPending.Contains(c.Status) && c.CreatedAt < now.AddDays(-s.RetentionDays)
                         select c.Id).Take(batch).ToListAsync(ct);
        if (due.Count == 0) return 0;
        // Guarded again in the update: a row that went back to pending in between (it cannot today) or was blanked already is left alone.
        return await db.SessionCommands.Where(c => due.Contains(c.Id) && c.Body != null && !CommandPending.Contains(c.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Body, (string?)null), ct);
    }

    private static async Task<int> PermissionInputsAsync(PodiumDb db, DateTimeOffset now, int batch, CancellationToken ct)
    {
        // jsonb equality is not in EF's translation: the "not blank yet" test is SQL (the blank is the empty object, as account deletion writes it).
        var due = await db.Database.SqlQuery<Guid>($$"""
            SELECT p.id AS "Value" FROM permission_requests p JOIN workspace_settings s ON s.workspace_id = p.workspace_id
            WHERE p.status <> {{PermissionStatuses.Open}} AND p.created_at < {{now}} - make_interval(days => s.retention_days) AND (p.tool_input <> '{}'::jsonb OR p.answers IS NOT NULL)
            LIMIT {{batch}}
            """).ToListAsync(ct);
        if (due.Count == 0) return 0;
        using var blank = JsonDocument.Parse(BlankInput);
        return await db.PermissionRequests.Where(p => due.Contains(p.Id) && p.Status != PermissionStatuses.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ToolInput, blank).SetProperty(p => p.Answers, (string?)null), ct);
    }

    private static async Task<int> ArchiveFilesAsync(PodiumDb db, string archiveDir, ILogger log, DateTimeOffset now, int batch, CancellationToken ct)
    {
        var due = await (from a in db.EventArchives.AsNoTracking()
                         join s in db.WorkspaceSettings.AsNoTracking() on a.WorkspaceId equals s.WorkspaceId
                         where a.CreatedAt < now.AddDays(-s.RetentionDays)
                         orderby a.CreatedAt, a.Id
                         select new { a.Id, a.Path }).Take(batch).ToListAsync(ct);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(archiveDir)) + Path.DirectorySeparatorChar;
        var changed = 0;
        foreach (var archive in due)
        {
            // The file first, then its row: a crash in between leaves a row whose file is gone, and the next pass deletes the row (File.Delete
            // of a missing file is no error). A file that cannot be deleted keeps its row for the next pass and does not stop the files after it.
            // Only a file under the archive folder is ever deleted: a row pointing anywhere else loses its row like a missing file (so it is not
            // tried every pass and never holds up the rows after it) and its file stays. The log names the archive, never its path.
            try
            {
                if (UnderRoot(archive.Path, root)) File.Delete(archive.Path);
                else log.LogWarning("archive {Archive} is not under the archive folder: its file was left alone", archive.Id);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                log.LogWarning("archive {Archive} could not be deleted ({Error}): it is tried again next pass", archive.Id, e.GetType().Name);
                continue;
            }

            changed += await db.EventArchives.Where(a => a.Id == archive.Id).ExecuteDeleteAsync(ct);
        }

        return changed;
    }

    /// <summary>Whether the path, made absolute (".." resolved), lies inside the archive folder (<paramref name="root"/> ends with a separator).</summary>
    private static bool UnderRoot(string path, string root) => Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal);
}
