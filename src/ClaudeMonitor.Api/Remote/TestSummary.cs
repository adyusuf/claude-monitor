using System.Text.RegularExpressions;

namespace ClaudeMonitor.Api.Remote;

/// <summary>The passed, failed and skipped counts of a test run (ADR-0008 §9.16).</summary>
public sealed record TestCounts(int Passed, int Failed, int Skipped);

/// <summary>
/// Reads the counts from the tail of a test run's output for the tools whose summary line is known: dotnet test
/// (<c>Failed: 0, Passed: 6, Skipped: 0, Total: 6</c>, summed over projects), vitest and jest (<c>Tests  1 failed | 4 passed (5)</c>,
/// <c>Tests: 1 failed, 4 passed, 5 total</c>) and pytest (<c>=== 1 failed, 5 passed in 0.5s ===</c>). Anything else leaves the counts
/// empty: a parsed count is a convenience, the exit code stays the result.
/// </summary>
public static partial class TestSummary
{
    public const int InputMax = 64 * 1024;

    public static TestCounts? Parse(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        var text = Ansi().Replace(output.Length > InputMax ? output[^InputMax..] : output, "");
        var dotnet = Dotnet().Matches(text);
        if (dotnet.Count > 0)
        {
            return new TestCounts(dotnet.Sum(m => Int(m.Groups[2])), dotnet.Sum(m => Int(m.Groups[1])), dotnet.Sum(m => Int(m.Groups[3])));
        }

        foreach (var line in text.Split('\n').Reverse())
        {
            if (!(Js().IsMatch(line) || Pytest().IsMatch(line))) continue;
            var tokens = Token().Matches(line);
            if (tokens.Count == 0) continue;
            int Sum(params string[] words) => tokens.Where(t => words.Contains(t.Groups[2].Value, StringComparer.OrdinalIgnoreCase)).Sum(t => Int(t.Groups[1]));
            return new TestCounts(Sum("passed"), Sum("failed", "error", "errors"), Sum("skipped", "todo"));
        }

        return null;
    }

    private static int Int(Group g) => int.TryParse(g.Value, out var n) ? Math.Min(n, 10_000_000) : 0;

    [GeneratedRegex("\u001b\\[[0-9;]*[A-Za-z]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Ansi();

    [GeneratedRegex(@"Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Dotnet();

    [GeneratedRegex(@"^\s*Tests:?\s+\d+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Js();

    [GeneratedRegex(@"\d+ (passed|failed)[^\n]* in \d+(\.\d+)?s", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Pytest();

    [GeneratedRegex(@"(\d+) (passed|failed|skipped|todo|errors?)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Token();
}
