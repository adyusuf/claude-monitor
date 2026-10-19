using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Agent.Push;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>The daemon's loops: a retried unit of work, the stop request, the API stream, and the backoff they share.</summary>
public sealed partial class DaemonHost
{
    private async Task Loop(string name, TimeSpan every, Func<CancellationToken, Task> work, ApiClient api, CancellationTokenSource cts,
        Action<string>? onFailure = null)
    {
        var failures = 0;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await work(cts.Token);
                failures = 0;
            }
            catch (Exception e)
            {
                if (cts.IsCancellationRequested) break; // stopping: whatever the request in flight ended with is not a failure
                // Only the daemon's own token means stopping; a timeout (also an OperationCanceledException) is a failure.
                failures++;
                log.Write($"{name} failed ({failures}): {e.GetType().Name} {e.Message}");
                onFailure?.Invoke(e.GetType().Name);
            }

            if (api.Disconnected) await cts.CancelAsync();
            await Wait(Backoff(every, failures), cts.Token);
        }
    }

    /// <summary>Ends the daemon when an update (or a rollback) asks for it by writing the stop-request file.</summary>
    private async Task StopRequestAsync(CancellationTokenSource cts)
    {
        while (!cts.IsCancellationRequested)
        {
            if (File.Exists(config.StopRequestPath))
            {
                TryDelete(config.StopRequestPath);
                log.Write("stop requested (an update)");
                await cts.CancelAsync();
                return;
            }

            await Wait(config.StopPollEvery, cts.Token);
        }
    }

    /// <summary>A request written before this daemon took its lock was meant for an earlier one; one written after is ours and stays.</summary>
    private void DropStaleStopRequest(DateTimeOffset started)
    {
        try
        {
            if (!File.Exists(config.StopRequestPath)) return;
            var at = DateTimeOffset.TryParse(File.ReadAllText(config.StopRequestPath), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var written) ? written : (DateTimeOffset?)null;
            if (at is null || at < started) File.Delete(config.StopRequestPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private async Task StreamAsync(ApiClient api, Relay relay, CancellationTokenSource cts)
    {
        var failures = 0;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await foreach (var (name, data) in api.StreamAsync(cts.Token, () => relay.StreamState(PushStatus.Connected, 0, null)))
                {
                    failures = 0;
                    if (!await relay.OnStreamAsync(name, data, cts.Token))
                    {
                        log.Write("revoked from the web: the API sent a revoke notice (the refresh credentials were used twice, so it revoked this agent); stop any other copy, then run `cm-agent login --server <url>`");
                        await cts.CancelAsync();
                        return;
                    }
                }

                relay.StreamState(PushStatus.Reconnecting, failures, "StreamClosed"); // the API ended it: reopen
            }
            catch (Exception e)
            {
                if (cts.IsCancellationRequested) return; // stopping, as above
                // A connect timeout is an OperationCanceledException too; it must not end the stream for good.
                failures++;
                log.Write($"stream failed ({failures}): {e.GetType().Name} {e.Message}");
                relay.StreamState(PushStatus.Reconnecting, failures, e.GetType().Name);
            }

            if (api.Disconnected) await cts.CancelAsync();
            await Wait(Backoff(TimeSpan.FromSeconds(1), failures), cts.Token);
        }
    }

    /// <summary>Doubles the wait per consecutive failure, up to the configured ceiling.</summary>
    internal TimeSpan Backoff(TimeSpan every, int failures) =>
        failures == 0 ? every : TimeSpan.FromTicks(Math.Min(config.RetryMax.Ticks, every.Ticks * (1L << Math.Min(failures, 16))));

    private async Task Wait(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, clock, ct);
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }
}
