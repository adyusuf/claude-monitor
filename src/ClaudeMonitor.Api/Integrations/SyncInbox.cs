using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ClaudeMonitor.Api.Integrations;

public enum SyncTarget
{
    Issue,
    Sprint,
}

public sealed record SyncRequest(Guid ConnectionId, SyncTarget Target, string Ref);

/// <summary>
/// "Read this issue or sprint again", queued by a webhook and taken by <see cref="SyncWorker"/>. In memory on purpose: a lost request costs
/// nothing, the next pass finds the change (ADR-0015 §6). The same request is queued once while it waits.
/// </summary>
public sealed class SyncInbox
{
    public const int Capacity = 1000;
    private readonly Channel<SyncRequest> channel = Channel.CreateBounded<SyncRequest>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });
    private readonly ConcurrentDictionary<SyncRequest, byte> waiting = new();

    public bool Enqueue(SyncRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!waiting.TryAdd(request, 0)) return false;
        if (channel.Writer.TryWrite(request)) return true;
        waiting.TryRemove(request, out _);
        return false;
    }

    public async Task<SyncRequest> TakeAsync(CancellationToken ct)
    {
        var request = await channel.Reader.ReadAsync(ct);
        waiting.TryRemove(request, out _);
        return request;
    }
}
