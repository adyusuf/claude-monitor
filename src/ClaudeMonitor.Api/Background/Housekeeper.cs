using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Background;

/// <summary>
/// Small periodic chores: expire commands, permission requests and device codes that ran out of time, and keep the
/// monthly partitions of session_events created ahead of need (the default partition catches anything else).
/// </summary>
public sealed class Housekeeper(IServiceScopeFactory scopes, TimeProvider clock, ILogger<Housekeeper> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);
    public const int MonthsAhead = 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await RunOnceAsync(scope.ServiceProvider.GetRequiredService<MonitorDb>(), clock.GetUtcNow(), stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "housekeeping failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public static async Task RunOnceAsync(MonitorDb db, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.SessionCommands
            .Where(c => (c.Status == CommandStatuses.Queued || c.Status == CommandStatuses.Delivered) && c.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CommandStatuses.Expired), ct);
        await db.PermissionRequests.Where(p => p.Status == PermissionStatuses.Open && p.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PermissionStatuses.Expired), ct);
        await db.DeviceAuthorizations
            .Where(d => (d.Status == DeviceStatuses.Pending || d.Status == DeviceStatuses.Approved) && d.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeviceStatuses.Expired), ct);
        await EnsurePartitionsAsync(db, now, ct);
    }

    /// <summary>session_events_yYYYYmMM for this month and the next ones. Idempotent.</summary>
    public static async Task EnsurePartitionsAsync(MonitorDb db, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i <= MonthsAhead; i++)
        {
            var from = month.AddMonths(i);
            var to = from.AddMonths(1);
            var name = $"session_events_y{from.Year:D4}m{from.Month:D2}";
            var exists = await db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\" FROM pg_class WHERE relname = {name}").AnyAsync(ct);
            if (exists)
            {
                continue;
            }

            var sql = $"CREATE TABLE IF NOT EXISTS {name} PARTITION OF session_events FOR VALUES FROM ('{from:yyyy-MM-dd}') TO ('{to:yyyy-MM-dd}')";
            try
            {
                await db.Database.ExecuteSqlRawAsync(sql, ct);
            }
            catch (Npgsql.PostgresException e) when (e.SqlState == "23514")
            {
                // Rows for that month already sit in the default partition; they stay there (still queryable).
            }
        }
    }
}
