using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ClaudeMonitor.Api.Streaming;

/// <summary>Turns a subscription (after an initial snapshot) into a server-sent event stream with keep-alive pings.</summary>
public static class Sse
{
    public static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IResult Stream(Subscription subscription, IEnumerable<StreamMessage> first, CancellationToken ct) =>
        TypedResults.ServerSentEvents(Items(subscription, first, ct));

    private static async IAsyncEnumerable<SseItem<string>> Items(Subscription subscription, IEnumerable<StreamMessage> first,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using (subscription)
        {
            foreach (var message in first)
            {
                yield return Item(message);
            }

            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(PingEvery);
                StreamMessage? next;
                try
                {
                    next = await subscription.Reader.ReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    next = null;
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                catch (System.Threading.Channels.ChannelClosedException)
                {
                    yield break;
                }

                yield return next is null ? new SseItem<string>("{}", "ping") : Item(next);
            }
        }
    }

    private static SseItem<string> Item(StreamMessage m) => new(JsonSerializer.Serialize(m.Data, Json), m.Event);
}
