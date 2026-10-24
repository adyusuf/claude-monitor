using Podium.Api.Config;
using Podium.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Integrations;

/// <summary>
/// Runs the reconciliation pass for every active connection that is due (ADR-0015 §6), also removing receipts and stale sign-in states. It is the
/// only update path where Jira cannot reach the API, and the net under the webhooks everywhere else.
/// </summary>
public sealed class Reconciler(IServiceScopeFactory scopes, ApiConfig config, IntegrationSettings settings, TimeProvider clock, ILogger<Reconciler> log)
    : BackgroundService
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
            // Always running since ADR-0017 (the Jira app may be saved later): without one there is nothing to pass, only the clean-up below.
            List<Guid> ids = settings.JiraAvailable
                ? await db.IntegrationConnections.AsNoTracking()
                    .Where(c => c.Status == ConnectionStatuses.Active && (c.LastPassAt == null || c.LastPassAt <= due)).Select(c => c.Id).ToListAsync(ct)
                : [];
            foreach (var id in ids)
            {
                // One connection at a time: a connection that keeps failing must not starve the ones after it (its run is closed as failed by the pass).
                try
                {
                    var connection = await db.IntegrationConnections.AsNoTracking().FirstAsync(c => c.Id == id, ct);
                    await scope.ServiceProvider.GetRequiredService<SyncService>().PassAsync(connection, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning("Integration pass of {Connection} failed: {Type}", id, e.GetType().Name);
                    // The connections of a tick share this scope's context: what the failed pass left tracked (a run row not yet saved) must not
                    // be saved by the next connection's pass.
                    db.ChangeTracker.Clear();
                }
            }

            // The clean-up has its own try: it runs even when a pass failed, and its failure does not hide the passes' own.
            try
            {
                await db.IntegrationWebhookReceipts.Where(r => r.ReceivedAt < now - ReceiptKeep).ExecuteDeleteAsync(ct);
                await db.IntegrationOAuthStates.Where(s => s.ExpiresAt < now).ExecuteDeleteAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning("Integration clean-up failed: {Type}", e.GetType().Name);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Everything but a shutdown: the pass runs on every server since ADR-0017, and a database that is briefly down (a raw
            // NpgsqlException) must not stop the host. The next tick tries again.
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
