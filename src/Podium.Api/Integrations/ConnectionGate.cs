using System.Collections.Concurrent;

namespace Podium.Api.Integrations;

/// <summary>One import, webhook read or pass at a time per connection, so two runs never write the same mirrored rows (ADR-0015 §6).</summary>
public static class ConnectionGate
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();

    public static async Task<T> RunAsync<T>(Guid connectionId, Func<Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        var gate = Gates.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await work();
        }
        finally
        {
            gate.Release();
        }
    }
}
