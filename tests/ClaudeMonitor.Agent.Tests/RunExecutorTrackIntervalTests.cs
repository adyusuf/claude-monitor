using ClaudeMonitor.Agent.Exec;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The descendant tracker's polling interval is bounded so that a mistake cannot make it spin or never look; zero turns it off.</summary>
public sealed class RunExecutorTrackIntervalTests
{
    [Theory]
    [InlineData(1, 50)]
    [InlineData(49, 50)]
    [InlineData(50, 50)]
    [InlineData(250, 250)]
    [InlineData(5000, 5000)]
    [InlineData(5001, 5000)]
    [InlineData(3_600_000, 5000)]
    public void An_interval_outside_fifty_milliseconds_to_five_seconds_is_brought_to_the_nearest_bound(int configuredMs, int expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), RunExecutor.TrackInterval(TimeSpan.FromMilliseconds(configuredMs)));
    }

    [Fact]
    public void An_interval_of_zero_or_less_turns_the_tracker_off()
    {
        Assert.Equal(TimeSpan.Zero, RunExecutor.TrackInterval(TimeSpan.Zero));
        Assert.Equal(TimeSpan.Zero, RunExecutor.TrackInterval(TimeSpan.FromSeconds(-1)));
    }
}
