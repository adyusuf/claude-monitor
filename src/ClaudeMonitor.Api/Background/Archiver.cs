using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Background;

/// <summary>
/// Retention (maintainer's decision, 03/10/2026): events older than the workspace's retention period are written,
/// one file per workspace per day (UTC, by arrival), as a zipped JSON array, and then deleted from the database in the
/// same transaction that records the archive (event_archives: path, count, size, SHA-256). The file is written to a
/// temporary name and moved into place before the rows are deleted, so a crash leaves either the rows or a file
/// plus the rows, never neither.
/// </summary>
public sealed class Archiver(IServiceScopeFactory scopes, ApiConfig config, TimeProvider clock, ILogger<Archiver> log)
    : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(6);
    public const int ChunkSize = 1000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var written = await RunOnceAsync(scope.ServiceProvider.GetRequiredService<MonitorDb>(), config.ArchiveDir,
                    clock.GetUtcNow(), stoppingToken);
                if (written > 0) log.LogInformation("archived {Days} workspace-day(s)", written);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "archiving failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Archives every complete workspace-day that is past retention. Returns how many files it wrote.</summary>
    public static async Task<int> RunOnceAsync(MonitorDb db, string archiveDir, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var written = 0;
        var settings = await db.WorkspaceSettings.AsNoTracking().ToListAsync(ct);
        foreach (var s in settings)
        {
            var cutoff = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(-s.RetentionDays);
            while (true)
            {
                var oldest = await db.SessionEvents.AsNoTracking()
                    .Where(e => e.WorkspaceId == s.WorkspaceId && e.ReceivedAt < cutoff)
                    .OrderBy(e => e.ReceivedAt).Select(e => (DateTimeOffset?)e.ReceivedAt).FirstOrDefaultAsync(ct);
                if (oldest is null) break;
                var day = DateOnly.FromDateTime(oldest.Value.UtcDateTime);
                await ArchiveDayAsync(db, archiveDir, s.WorkspaceId, day, now, ct);
                written++;
            }
        }

        return written;
    }

    private static async Task ArchiveDayAsync(MonitorDb db, string archiveDir, Guid workspaceId, DateOnly day, DateTimeOffset now,
        CancellationToken ct)
    {
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = from.AddDays(1);
        var archive = new EventArchive { WorkspaceId = workspaceId, Day = day, CreatedAt = now };
        var dir = Path.Combine(archiveDir, workspaceId.ToString(), $"{day:yyyy}", $"{day:MM}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{day:yyyy-MM-dd}-{archive.Id:N}.json.zip");
        var temp = path + ".partial";

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (count, lastId) = await WriteZipAsync(db, temp, workspaceId, from, to, ct);
        File.Move(temp, path);
        archive.Path = path;
        archive.EventCount = count;
        archive.Bytes = new FileInfo(path).Length;
        await using (var file = File.OpenRead(path))
        {
            archive.Sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        }

        db.EventArchives.Add(archive);
        await db.SaveChangesAsync(ct);
        await db.SessionEvents.Where(e => e.WorkspaceId == workspaceId && e.ReceivedAt >= from && e.ReceivedAt < to && e.Id <= lastId)
            .ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static async Task<(int Count, long LastId)> WriteZipAsync(MonitorDb db, string temp, Guid workspaceId,
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var count = 0;
        long lastId = 0;
        await using (var stream = File.Create(temp))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry($"{from:yyyy-MM-dd}.json", CompressionLevel.SmallestSize);
            await using var entryStream = entry.Open();
            await using var writer = new Utf8JsonWriter(entryStream);
            writer.WriteStartArray();
            while (true)
            {
                var chunk = await db.SessionEvents.AsNoTracking()
                    .Where(e => e.WorkspaceId == workspaceId && e.ReceivedAt >= from && e.ReceivedAt < to && e.Id > lastId)
                    .OrderBy(e => e.Id).Take(ChunkSize).ToListAsync(ct);
                if (chunk.Count == 0) break;
                foreach (var e in chunk)
                {
                    JsonSerializer.Serialize(writer, new
                    {
                        e.Id,
                        e.SessionId,
                        e.Kind,
                        e.OccurredAt,
                        e.ReceivedAt,
                        e.Truncated,
                        Payload = e.Payload.RootElement,
                    }, Json);
                    e.Payload.Dispose();
                }

                count += chunk.Count;
                lastId = chunk[^1].Id;
            }

            writer.WriteEndArray();
        }

        return (count, lastId);
    }
}
