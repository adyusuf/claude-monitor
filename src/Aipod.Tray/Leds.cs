namespace Aipod.Tray;

/// <summary>Which of the two lights is lit at this moment (orange: a message arrived; green: a message left or reached a session).</summary>
public readonly record struct Leds(bool Orange, bool Green)
{
    public static readonly Leds Off = new(false, false);
}

/// <summary>
/// Turns the agent's two message counters into blinking lights. A counter that goes up starts a blink of <see cref="Active"/> in which the light is on for
/// <see cref="Half"/>, off for <see cref="Half"/>, and so on, starting lit; the first reading and a counter that went down (a reset) only set the starting
/// point, so opening the tray never flashes for old messages.
/// </summary>
public sealed class LedBlinker(TimeProvider clock)
{
    public static readonly TimeSpan Active = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan Half = TimeSpan.FromMilliseconds(300);

    private long? incoming;
    private long? outgoing;
    private DateTimeOffset incomingSince = DateTimeOffset.MinValue;
    private DateTimeOffset outgoingSince = DateTimeOffset.MinValue;

    public void Observe(long incomingCount, long outgoingCount)
    {
        var now = clock.GetUtcNow();
        if (incoming is { } before && incomingCount > before) incomingSince = now;
        if (outgoing is { } sent && outgoingCount > sent) outgoingSince = now;
        incoming = incomingCount;
        outgoing = outgoingCount;
    }

    public Leds Now()
    {
        var now = clock.GetUtcNow();
        return new Leds(Lit(now - incomingSince), Lit(now - outgoingSince));
    }

    /// <summary>True while a blink is running and its current half is a lit one.</summary>
    public static bool Lit(TimeSpan sinceStart) =>
        sinceStart >= TimeSpan.Zero && sinceStart < Active && (long)(sinceStart / Half) % 2 == 0;
}
