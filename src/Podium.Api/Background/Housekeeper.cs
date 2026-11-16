using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Streaming;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Background;

/// <summary>
/// Small periodic chores: expire commands, permission requests and device codes that ran out of time, delete ended agent tokens, device codes and account tokens,
/// keep the monthly partitions of session_events created ahead of need (the default partition catches anything else), and blank captured content past retention
/// (<see cref="ContentRetention"/>).
/// </summary>
public sealed class Housekeeper(IServiceScopeFactory scopes, TimeProvider clock, ILogger<Housekeeper> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);
    public const int MonthsAhead = 2;

    /// <summary>How long an ended agent token or device code is kept (for the log of what happened) before it is deleted.</summary>
    public static readonly TimeSpan KeepEndedCredentials = TimeSpan.FromDays(7);

    /// <summary>How long an expired or spent account mail or sign-in token (user_tokens: verification, reset, two-step pending) is kept before it is deleted (F-185).</summary>
    public static readonly TimeSpan KeepEndedUserTokens = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await PassAsync(scope.ServiceProvider, clock, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "housekeeping failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass of every chore, these and the remote ones, in one scope.</summary>
    public static async Task PassAsync(IServiceProvider services, TimeProvider clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        var db = services.GetRequiredService<PodiumDb>();
        await Chores.RunAsync(db, ct,
            () => RunOnceAsync(db, clock.GetUtcNow(), ct, services.GetRequiredService<Broker>()),
            () => RemoteHousekeeping.RunOnceAsync(db, services.GetRequiredService<ApiConfig>(), services.GetRequiredService<Broker>(), clock.GetUtcNow(), ct));
    }

    /// <summary>Each chore runs even when one before it fails (<see cref="Chores"/>).</summary>
    public static async Task RunOnceAsync(PodiumDb db, DateTimeOffset now, CancellationToken ct, Broker? broker = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        // An approval rule holds a command the session ran: a week after it ended (revoked or expired) it is gone (ADR-0019).
        var ruleCutoff = now - ApprovalRuleLimits.KeepEnded;
        var credentialCutoff = now - KeepEndedCredentials;
        var userTokenCutoff = now - KeepEndedUserTokens;
        await Chores.RunAsync(db, ct,
            () => SessionExpiry.CommandsAsync(db, now, broker, ct),
            () => SessionExpiry.PermissionsAsync(db, now, broker, ct),
            () => db.ApprovalRules.Where(r => (r.RevokedAt != null && r.RevokedAt <= ruleCutoff) || (r.ExpiresAt != null && r.ExpiresAt <= ruleCutoff))
                .ExecuteDeleteAsync(ct),
            () => db.DeviceAuthorizations
                .Where(d => (d.Status == DeviceStatuses.Pending || d.Status == DeviceStatuses.Approved) && d.ExpiresAt <= now)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeviceStatuses.Expired), ct),
            // Agent tokens and device codes are kept a while after they end, then deleted; a replaced refresh token ends with its expiry,
            // so a copied one is still recognised as a reuse for as long as it could have worked.
            () => db.AgentTokens.Where(t => t.ExpiresAt <= credentialCutoff || (t.RevokedAt != null && t.RevokedAt <= credentialCutoff))
                .ExecuteDeleteAsync(ct),
            () => db.DeviceAuthorizations.Where(d => d.ExpiresAt <= credentialCutoff).ExecuteDeleteAsync(ct),
            () => db.UserTokens.Where(t => t.ExpiresAt <= userTokenCutoff || (t.UsedAt != null && t.UsedAt <= userTokenCutoff)).ExecuteDeleteAsync(ct),
            () => EnsurePartitionsAsync(db, now, ct),
            // Prompt texts, permission inputs and archive files past the workspace's retention (F-119).
            () => ContentRetention.RunOnceAsync(db, now, ct));
    }

    /// <summary>session_events_yYYYYmMM for this month and the next ones. Idempotent.</summary>
    public static async Task EnsurePartitionsAsync(PodiumDb db, DateTimeOffset now, CancellationToken ct)
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
