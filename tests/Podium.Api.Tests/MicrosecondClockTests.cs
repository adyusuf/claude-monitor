using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>The test clock reads whole microseconds, as PostgreSQL keeps them, so a time the API wrote compares equal to the clock's own.</summary>
public sealed class MicrosecondClockTests
{
    private static readonly DateTimeOffset Sub = new(new DateTime(2026, 10, 24, 13, 37, 50, DateTimeKind.Utc).AddTicks(1139599), TimeSpan.Zero);

    [Fact]
    public void A_time_is_cut_down_to_whole_microseconds_and_a_whole_one_is_left_alone()
    {
        Assert.Equal(Sub.AddTicks(-9), Sub.ToMicroseconds());
        Assert.Equal(Sub.ToMicroseconds(), Sub.ToMicroseconds().ToMicroseconds());
        Assert.Equal(TimeSpan.Zero, Sub.ToMicroseconds().Offset);
    }

    [Fact]
    public void The_clock_starts_on_a_whole_microsecond_even_when_it_is_given_a_finer_time()
    {
        Assert.Equal(0, new TestClock(Sub).GetUtcNow().Ticks % TimeSpan.TicksPerMicrosecond);
    }

    [Fact]
    public void The_clock_stays_on_a_whole_microsecond_when_it_is_advanced_by_less_than_one()
    {
        var clock = new TestClock(Sub);
        var before = clock.GetUtcNow();

        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(7));

        Assert.Equal(before + TimeSpan.FromMinutes(10), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromTicks(13));
        Assert.Equal(before + TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(10), clock.GetUtcNow());
    }
}
