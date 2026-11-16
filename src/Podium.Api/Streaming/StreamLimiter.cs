namespace Podium.Api.Streaming;

/// <summary>
/// How many workspace event streams one member holds open at once (F-254): every stream is a broker subscriber with its own channel, so
/// without a cap one person could hold any number. A stream takes a lease when it opens and gives it back when it ends, however it ends.
/// </summary>
public sealed class StreamLimiter
{
    /// <summary>The most workspace streams one member has open at once (several tabs and devices fit); the next one is answered 429 <see cref="TooManyStreams"/>.</summary>
    public const int MaxPerMember = 20;

    /// <summary>429 title: the member already holds <see cref="MaxPerMember"/> streams.</summary>
    public const string TooManyStreams = "too_many_streams";

    private readonly Dictionary<Guid, int> open = [];

    /// <summary>A lease on one of the member's stream places, or null when they hold <see cref="MaxPerMember"/> already. Dispose it to give the place back (more than once is fine).</summary>
    public IDisposable? TryOpen(Guid userId)
    {
        lock (open)
        {
            var held = open.GetValueOrDefault(userId);
            if (held >= MaxPerMember) return null;
            open[userId] = held + 1;
        }

        return new Lease(this, userId);
    }

    public int Open(Guid userId)
    {
        lock (open) return open.GetValueOrDefault(userId);
    }

    private void Release(Guid userId)
    {
        lock (open)
        {
            if (!open.TryGetValue(userId, out var held)) return;
            if (held <= 1) open.Remove(userId);
            else open[userId] = held - 1;
        }
    }

    private sealed class Lease(StreamLimiter owner, Guid userId) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) owner.Release(userId);
        }
    }
}
