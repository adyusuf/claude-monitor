using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Update;

public sealed partial class Updater
{
    private async Task<UpdateResult> ReplaceAndWatchAsync(UpdateOffer offer, string staged, CancellationToken ct)
    {
        var from = AgentConfig.Version;
        var style = BinarySwap.For(config.UpdateOs);
        var wasRunning = daemon.IsRunning();
        var connected = Auth.Identity.Load(config).Connected;
        BinarySwap.Install(config.BinaryPath, staged, style);
        UpdateState.Change(config, s => s with { Phase = UpdateState.PendingHealth, From = from, To = offer.Version, Result = UpdateCodes.Available });
        log.Write($"update: {from} -> {offer.Version} put in place, restarting the daemon");

        if (!wasRunning && !connected) return Record(Installed(from, offer.Version, "replaced; no daemon to restart"));
        if (wasRunning && !await daemon.StopAsync(config.UpdateStopWait, ct))
        {
            return Record(Installed(from, offer.Version, "replaced; the running daemon did not stop and keeps the old version until it restarts"));
        }

        // Taken only now that the old daemon is gone: its last heartbeat must not count for the new one.
        var since = clock.GetUtcNow();
        daemon.Start(config.BinaryPath);
        switch (await WatchAsync(offer.Version, since, ct))
        {
            case Health.Healthy:
                return Record(Installed(from, offer.Version, "the new daemon is healthy"));
            case Health.Inconclusive:
                // Running, but the network (not the API) kept it from being answered: that says nothing against the build.
                return Record(Installed(from, offer.Version, "the new daemon runs; the API could not be reached yet, so it is not confirmed (the previous version stays in .prev)"));
            default:
                return await RollBackAsync(offer, from, style, ct);
        }
    }

    private async Task<UpdateResult> RollBackAsync(UpdateOffer offer, string from, SwapStyle style, CancellationToken ct)
    {
        log.Write($"update: {offer.Version} is not healthy after {config.UpdateHealthWait.TotalSeconds:0} s, rolling back to {from}");
        var stopped = await daemon.StopAsync(config.UpdateStopWait, ct) || daemon.Kill();
        BinarySwap.Restore(config.BinaryPath, style);
        UpdateState.Change(config, s => s with { Phase = null, BlockedVersion = offer.Version, To = offer.Version, From = from });
        if (!stopped)
        {
            // The old binary is back for every hook, but a hung process still holds the daemon's lock: no daemon can start until it ends.
            return Record(new(UpdateCodes.RollbackStuck, $"{offer.Version} is back on {from} on disk, but its hung daemon would not stop; end the cm-agent process by hand", from, offer.Version));
        }

        daemon.Start(config.BinaryPath);
        return Record(new(UpdateCodes.RolledBack, $"{offer.Version} did not become healthy within {config.UpdateHealthWait.TotalSeconds:0} s; back on {from}", from, offer.Version));
    }

    private enum Health { Healthy, Inconclusive, Unhealthy }

    /// <summary>
    /// Healthy: the new daemon reports its version and the API answered it since it started. Not healthy: it is gone, did not
    /// report its version, or the API refused it. Inconclusive: it runs but its heartbeats failed only for lack of a network
    /// (a rollback then would punish a good build for a flaky link and block it for good).
    /// </summary>
    private async Task<Health> WatchAsync(string version, DateTimeOffset since, CancellationToken ct)
    {
        using var store = new LocalStore(config.DatabasePath);
        var until = clock.GetUtcNow() + config.UpdateHealthWait;
        while (true)
        {
            if (UpdateHealth.IsHealthy(store, version, since)) return Health.Healthy;
            if (clock.GetUtcNow() >= until) break;
            await Task.Delay(TimeSpan.FromSeconds(1), clock, ct);
        }

        if (!daemon.IsRunning() || store.Get(UpdateHealth.VersionKey) != version) return Health.Unhealthy;
        return UpdateHealth.NetworkErrors.Contains(store.Get(Relay.HeartbeatErrorKey) ?? "") ? Health.Inconclusive : Health.Unhealthy;
    }

    private static UpdateResult Installed(string from, string to, string note) => new(UpdateCodes.Installed, $"{from} -> {to}: {note}", from, to);

    /// <summary>Writes the outcome to update-state.json and agent.log and passes it on.</summary>
    private UpdateResult Record(UpdateResult result)
    {
        UpdateState.Change(config, s => s with
        {
            Result = result.Code,
            Detail = result.Message,
            Phase = null,
            Available = result.Succeeded ? null : s.Available,
            InstalledAt = result.Succeeded ? Now() : s.InstalledAt,
        });
        log.Write($"update {result.Code}: {result.Message}");
        return result;
    }

    private void Cleanup(string staged)
    {
        try
        {
            File.Delete(staged);
            if (Directory.Exists(config.UpdateDir)) Directory.Delete(config.UpdateDir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the next update starts with a clean folder anyway
        }
    }

    private string Now() => clock.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
