namespace Podium.Api.Tests.Infrastructure;

/// <summary>
/// PostgreSQL keeps a timestamp to the microsecond while .NET keeps 100 ns ticks (Linux clocks read all seven digits), so a time that is
/// written and read back loses its last digit. Tests that compare such a time with one made in memory go through here.
/// </summary>
public static class Microseconds
{
    /// <summary>The time cut down to whole microseconds, which is what the database returns for it.</summary>
    public static DateTimeOffset ToMicroseconds(this DateTimeOffset value) => value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));
}
