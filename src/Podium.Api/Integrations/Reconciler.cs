using Podium.Api.Config;
using Podium.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Integrations;

/// <summary>
/// Runs the reconciliation pass for every active connection that is due (ADR-0015 §6), also removing receipts and stale sign-in states. It is the
/// only update path where Jira cannot reach the API, and the net under the webhooks everywhere else.
/// </summary>
public sealed class Reconciler(IServiceScopeFactory scopes, ApiConfig config, TimeProvider clock, ILogger<Reconciler> log) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan ReceiptKeep = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PodiumDb>();
            var now = clock.GetUtcNow();
            var due = now - config.IntegrationPassEvery;
            var ids = await db.IntegrationConnections.AsNoTracking()
                .Where(c => c.Status == ConnectionStatuses.Active && (c.LastPassAt == null || c.LastPassAt <= due)).Select(c => c.Id).ToListAsync(ct);
            foreach (var id in ids)
            {
                var connection = await db.IntegrationConnections.AsNoTracking().FirstAsync(c => c.Id == id, ct);
                await scope.ServiceProvider.GetRequiredService<SyncService>().PassAsync(connection, ct);
            }

            await db.IntegrationWebhookReceipts.Where(r => r.ReceivedAt < now - ReceiptKeep).ExecuteDeleteAsync(ct);
            await db.IntegrationOAuthStates.Where(s => s.ExpiresAt < now).ExecuteDeleteAsync(ct);
        }
        catch (Exception e) when (e is TrackerException or DbUpdateException or InvalidOperationException)
        {
            log.LogWarning("Integration reconciliation failed: {Type}", e.GetType().Name);
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
