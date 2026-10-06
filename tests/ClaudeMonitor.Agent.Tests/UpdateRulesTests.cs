using ClaudeMonitor.Agent.Update;

namespace ClaudeMonitor.Agent.Tests;

public sealed class UpdateRulesTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("0.0.0", 0, 0, 0)]
    [InlineData("0.10.0", 0, 10, 0)]
    [InlineData("999999999.0.1", 999999999, 0, 1)]
    public void A_plain_three_part_version_parses(string text, int major, int minor, int patch)
    {
        Assert.True(UpdateRules.TryParse(text, out var version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("-1.2.3")]
    [InlineData("+1.2.3")]
    [InlineData("1.2.3-beta")]
    [InlineData("1.2.3 ")]
    [InlineData("1..3")]
    [InlineData("")]
    [InlineData("1.2.9999999999")]
    [InlineData("99999999999999999999.0.0")]
    [InlineData("١.٢.٣")]
    public void Anything_else_is_not_a_version(string text)
    {
        Assert.False(UpdateRules.TryParse(text, out var version));
        Assert.Equal(new Version(0, 0, 0), version);
    }

    [Fact]
    public void Null_is_not_a_version()
    {
        Assert.False(UpdateRules.TryParse(null, out _));
    }

    [Theory]
    [InlineData("0.10.0", "0.9.0", true)] // numerically, not as text
    [InlineData("1.0.0", "0.99.99", true)]
    [InlineData("0.3.1", "0.3.0", true)]
    [InlineData("0.3.0", "0.3.0", false)]
    [InlineData("0.2.9", "0.3.0", false)]
    [InlineData("0.9.0", "0.10.0", false)]
    [InlineData("x", "0.3.0", false)]
    [InlineData("0.3.1", "x", false)]
    [InlineData("1.2.3-beta", "1.2.2", false)]
    [InlineData("", "", false)]
    [InlineData(null, "0.3.0", false)]
    [InlineData("0.3.1", null, false)]
    public void Only_a_strictly_higher_version_is_newer(string? candidate, string? current, bool expected)
    {
        Assert.Equal(expected, UpdateRules.IsNewer(candidate, current));
    }

    [Theory]
    [InlineData("0.4.0", "0.3.0", true)]
    [InlineData("0.10.0", "0.9.0", true)] // numerically
    [InlineData("0.9.0", "0.10.0", false)]
    [InlineData("0.3.0", "0.3.0", false)] // equal is still supported
    [InlineData("0.2.0", "0.3.0", false)]
    [InlineData("x", "0.3.0", false)]
    [InlineData("0.4.0", "x", false)]
    [InlineData(null, "0.3.0", false)]
    [InlineData("0.4.0", null, false)]
    public void Below_the_minimum_means_the_running_version_is_lower(string? minimum, string? current, bool expected)
    {
        Assert.Equal(expected, UpdateRules.IsBelowMinimum(minimum, current));
    }
}
