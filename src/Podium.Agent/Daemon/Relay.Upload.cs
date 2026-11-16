using System.Globalization;
using System.Net;
using Podium.Agent.Net;
using Podium.Agent.Push;
using Podium.Contracts;

namespace Podium.Agent.Daemon;

/// <summary>The upload of the outbox: batches of the size the API takes, a refused batch halved down to the one event that is refused, and that event moved aside.</summary>
public sealed partial class Relay
{
    /// <summary>The most events the server takes in one batch, as the last settings pass read it (0 or absent: unknown). Read through the workspace tag like the other switches.</summary>
    public const string BatchMaxEventsKey = "settings.batch_max_events";

    /// <summary>One event alone refused for good this many times in a row is moved aside; fewer, and the upload fails (and is retried after the loop's backoff, which grows with every failure: an API that refuses everything drains the outbox slowly and loudly, one logged event per three tries).</summary>
    public const int QuarantineAfterRefusals = 3;

    /// <summary>The answers that say "this request will never be accepted as it is" (not the transient ones: a timeout, a rate limit, an outage, an expired token).</summary>
    private static readonly HashSet<HttpStatusCode> RefusedForGood = [HttpStatusCode.BadRequest, HttpStatusCode.RequestEntityTooLarge, HttpStatusCode.UnprocessableEntity];

    // The batch size the API takes: halved each time it refuses a batch as too large (its MaxBatchEvents is below ours), for this daemon's life.
    private int batchEvents = config.BatchEvents;

    private long refusedSeq;
    private int refusals;

    /// <summary>The batch size to build: ours, halved as far as the API has refused, and never above the size the API said it takes.</summary>
    private int BatchSize() =>
        WorkspaceSettings.Get(config, store, BatchMaxEventsKey) is { } told && int.TryParse(told, NumberStyles.None, CultureInfo.InvariantCulture, out var max) && max > 0
            ? Math.Min(batchEvents, max)
            : batchEvents;

    /// <summary>Sends batches until the outbox is empty. Returns the number of events the API acknowledged.</summary>
    public async Task<int> UploadAsync(CancellationToken ct)
    {
        var sent = 0;
        while (store.NextBatch(BatchSize(), config.BatchBytes) is { } batch)
        {
            BatchAck ack;
            try
            {
                ack = await api.SendBatchAsync(new EventBatch(batch.Seq, batch.Rows.Select(r => r.Event).ToList()), ct);
            }
            catch (ApiException e) when (RefusedForGood.Contains(e.Status) && batch.Rows.Count > 1)
            {
                // Refused before anything was stored under its number: the same number goes up again with the first half only. Too large (its
                // MaxBatchEvents is below ours) also shrinks the batches after it; another refusal is one event's, and halving finds it.
                var half = Math.Max(1, batch.Rows.Count / 2);
                if (e.Status == HttpStatusCode.RequestEntityTooLarge) batchEvents = half;
                store.ShrinkBatch(batch.Seq, half);
                continue;
            }
            catch (ApiException e) when (RefusedForGood.Contains(e.Status))
            {
                // One event alone, refused for good more than once: it is moved aside and counted, so the events behind it are not held for ever.
                if (NextRefusal(batch.Seq) < QuarantineAfterRefusals) throw;
                new AgentLog(config, clock).Write($"event refused for good by the API (HTTP {(int)e.Status}, {refusals} tries) and moved aside: batch {batch.Seq}, kind {batch.Rows[0].Event.Kind}");
                store.Quarantine(batch.Seq, (int)e.Status, clock.GetUtcNow());
                refusals = 0;
                continue;
            }

            refusals = 0;
            store.Acknowledge(batch.Seq);
            sent += ack.Duplicate ? 0 : batch.Rows.Count;
            UploadOutcome(null);
        }

        return sent;
    }

    /// <summary>Counts one more refusal of the same one-event batch (a different one starts over).</summary>
    private int NextRefusal(long seq)
    {
        if (refusedSeq != seq) refusals = 0;
        refusedSeq = seq;
        return ++refusals;
    }

    /// <summary>Records the outcome of an upload pass: its time, or the failure's type name.</summary>
    public void UploadOutcome(string? error)
    {
        if (error is null)
        {
            store.Set(PushStatus.UploadAtKey, Now());
            store.Set(PushStatus.UploadErrorKey, "");
            return;
        }

        store.Set(PushStatus.UploadErrorKey, error);
        store.Set(PushStatus.UploadErrorAtKey, Now());
    }
}
