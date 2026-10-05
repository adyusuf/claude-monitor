using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;

namespace ClaudeMonitor.Agent.Push;

/// <summary>
/// The push loop of one MCP process (ADR-0003): every <see cref="AgentConfig.PushPollEvery"/>, once the connection is ready,
/// one <see cref="PushPump"/> pass. It reads a local file; the model is never involved. A failure of the local database is
/// reported and the next pass tries again.
/// </summary>
public static class PushLoop
{
    public static async Task RunAsync(AgentConfig config, TimeProvider clock, Func<bool> ready, Func<ChannelEnvelope, Task> send,
        string? environmentSession, Action<string> report, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(config);
        using var timer = new PeriodicTimer(config.PushPollEvery, clock);
        while (await WaitAsync(timer, stop))
        {
            if (!ready()) continue; // the handshake is not finished: a notification now would be lost
            try
            {
                using var store = new LocalStore(config.DatabasePath);
                await new PushPump(config, store, clock, ParentProcess.Id(), environmentSession).PumpAsync(send);
            }
            catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                report($"cm-agent push: {e.GetType().Name}"); // type name only; never content
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stop)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stop);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
