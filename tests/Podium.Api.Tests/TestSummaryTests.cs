using Podium.Api.Remote;

namespace Podium.Api.Tests;

/// <summary>The passed, failed and skipped counts read from the tail of a test run's output (ADR-0008 §9.16).</summary>
public sealed class TestSummaryTests
{
    [Fact]
    public void Dotnet_summaries_are_summed_over_the_projects_of_one_run()
    {
        var output = "Passed!  - Failed:     0, Passed:  1493, Skipped:     2, Total:  1495, Duration: 10 s - A.dll (net10.0)\n"
                     + "Failed!  - Failed:     3, Passed:   866, Skipped:     0, Total:   869, Duration: 2 m - B.dll (net10.0)\n";
        Assert.Equal(new TestCounts(2359, 3, 2), TestSummary.Parse(output));
    }

    [Fact]
    public void Vitest_and_jest_lines_are_read_with_their_failed_and_skipped_tokens()
    {
        Assert.Equal(new TestCounts(322, 1, 0), TestSummary.Parse(" Test Files  1 failed | 40 passed (41)\n      Tests  1 failed | 322 passed (323)\n   Duration  9s\n"));
        Assert.Equal(new TestCounts(4, 1, 2), TestSummary.Parse("Tests:       1 failed, 2 skipped, 4 passed, 7 total\nTime: 2 s\n"));
    }

    [Fact]
    public void Pytest_summaries_count_errors_as_failed()
    {
        Assert.Equal(new TestCounts(5, 2, 1), TestSummary.Parse("============ 1 failed, 1 error, 5 passed, 1 skipped in 0.52s ============\n"));
        Assert.Equal(new TestCounts(7, 0, 0), TestSummary.Parse("===== 7 passed in 0.10s =====\n"));
    }

    [Fact]
    public void Colour_codes_do_not_hide_the_summary()
    {
        Assert.Equal(new TestCounts(323, 0, 0), TestSummary.Parse("\u001b[2m      Tests \u001b[22m \u001b[1m\u001b[32m323 passed\u001b[39m\u001b[22m\u001b[90m (323)\u001b[39m\n".Replace("Tests ", "Tests  ")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ok, nothing to count here\nexit 0\n")]
    public void An_unknown_tool_leaves_the_counts_empty(string? output) => Assert.Null(TestSummary.Parse(output));

    [Fact]
    public void Only_the_tail_is_read_and_a_huge_number_is_capped()
    {
        var output = new string('x', 200_000) + "\nPassed!  - Failed: 0, Passed: 99999999999, Skipped: 0, Total: 1\n";
        var counts = TestSummary.Parse(output)!;
        Assert.Equal(0, counts.Passed);
    }
}
