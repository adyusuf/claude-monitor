namespace ClaudeMonitor.Api.Update;

/// <summary>Keeps the download host's manifest.json fresh (only registered when MONITOR_DOWNLOADS_URL is set): once at start, then every minute.</summary>
public sealed class UpdateCatalogRefresher(UpdateCatalog catalog) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        do
        {
            await catalog.RefreshAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
