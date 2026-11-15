using System.Diagnostics;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Podium.Api.Streaming;

/// <summary>
/// Turns a subscription (after an initial snapshot) into a server-sent event stream with keep-alive pings. When <c>stillAllowed</c> is given it
/// is asked at every ping, and at least once per ping interval on a busy stream; a "no" ends the stream (a removed member, a revoked login).
/// </summary>
public static class Sse
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IResult Stream(Subscription subscription, IEnumerable<StreamMessage> first, TimeSpan pingEvery, CancellationToken ct,
        Func<CancellationToken, Task<bool>>? stillAllowed = null) =>
        TypedResults.ServerSentEvents(Items(subscription, first, pingEvery, stillAllowed, ct));

    private static async IAsyncEnumerable<SseItem<string>> Items(Subscription subscription, IEnumerable<StreamMessage> first,
        TimeSpan pingEvery, Func<CancellationToken, Task<bool>>? stillAllowed, [EnumeratorCancellation] CancellationToken ct)
    {
        using (subscription)
        {
            var checkedAt = Stopwatch.GetTimestamp();
            foreach (var message in first)
            {
                yield return Item(message);
            }

            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(pingEvery);
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

                if (stillAllowed is not null && (next is null || Stopwatch.GetElapsedTime(checkedAt) >= pingEvery))
                {
                    if (!await stillAllowed(ct))
                    {
                        yield break;
                    }

                    checkedAt = Stopwatch.GetTimestamp();
                }

                yield return next is null ? new SseItem<string>("{}", "ping") : Item(next);
            }
        }
    }

    private static SseItem<string> Item(StreamMessage m) => new(JsonSerializer.Serialize(m.Data, Json), m.Event);
}
