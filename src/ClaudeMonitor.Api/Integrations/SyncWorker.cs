using ClaudeMonitor.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Integrations;

/// <summary>Takes the queued webhook requests one by one and reads the issue or sprint again (ADR-0015 §6).</summary>
public sealed class SyncWorker(IServiceScopeFactory scopes, SyncInbox inbox, ILogger<SyncWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            SyncRequest request;
            try
            {
                request = await inbox.TakeAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await HandleAsync(request, stoppingToken);
        }
    }

    internal async Task HandleAsync(SyncRequest request, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MonitorDb>();
            var connection = await db.IntegrationConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.ConnectionId && c.Status == ConnectionStatuses.Active, ct);
            if (connection is null) return;
            var sync = scope.ServiceProvider.GetRequiredService<SyncService>();
            if (request.Target == SyncTarget.Issue) await sync.SyncIssueAsync(connection, request.Ref, ct);
            else await sync.SyncSprintAsync(connection, request.Ref, ct);
        }
        catch (Exception e) when (e is TrackerException or DbUpdateException)
        {
            // The pass finds it again; the request is not worth stopping the worker.
            log.LogWarning("Webhook sync for {Connection} failed: {Type}", request.ConnectionId, e.GetType().Name);
        }
    }
}
