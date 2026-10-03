using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ClaudeMonitor.Api.Streaming;

/// <summary>One message on a stream: the SSE event name and its JSON-serialisable data.</summary>
public sealed record StreamMessage(string Event, object Data);

/// <summary>
/// In-process publish/subscribe for the live streams: "ws:{id}" for a workspace's web pages and "agent:{id}" for one
/// agent. Single-instance by design (ADR-0002 runs one API process per environment); every stream is also a
/// view over the database, so a client that reconnects reads the current state first and loses nothing.
/// A slow subscriber drops its oldest messages instead of slowing the publisher.
/// </summary>
public sealed class Broker
{
    private const int Capacity = 256;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<StreamMessage>>> topics = new();

    public static string Workspace(Guid id) => $"ws:{id}";

    public static string Agent(Guid id) => $"agent:{id}";

    public void Publish(string topic, StreamMessage message)
    {
        if (!topics.TryGetValue(topic, out var subscribers))
        {
            return;
        }

        foreach (var channel in subscribers.Values)
        {
            channel.Writer.TryWrite(message);
        }
    }

    public Subscription Subscribe(string topic)
    {
        var channel = Channel.CreateBounded<StreamMessage>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        var id = Guid.NewGuid();
        topics.GetOrAdd(topic, _ => new()).TryAdd(id, channel);
        return new Subscription(channel.Reader, () =>
        {
            if (topics.TryGetValue(topic, out var subscribers))
            {
                subscribers.TryRemove(id, out _);
            }

            channel.Writer.TryComplete();
        });
    }

    public int SubscriberCount(string topic) => topics.TryGetValue(topic, out var s) ? s.Count : 0;
}

public sealed class Subscription(ChannelReader<StreamMessage> reader, Action dispose) : IDisposable
{
    public ChannelReader<StreamMessage> Reader { get; } = reader;

    public void Dispose() => dispose();
}
