using System.ComponentModel;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Metrics;

/// <summary>
/// Reads this machine's CPU, memory and disks with the OS's own counters (ADR-0005, "Resources and alerts").
/// Sample returns null when the numbers are unavailable and never throws.
/// </summary>
public interface IMetricsSource
{
    MetricSample? Sample(DateTimeOffset now);
}

/// <summary>The failures a counter read can end in: all mean "no numbers now", none is a bug to crash for.</summary>
internal static class MetricFailure
{
    public static bool IsExpected(Exception e) => e is IOException or UnauthorizedAccessException or FormatException
        or OverflowException or ArgumentException or InvalidOperationException or NotSupportedException or Win32Exception
        or DllNotFoundException or EntryPointNotFoundException;
}

/// <summary>Cumulative busy and total CPU time in any one unit; only differences between two readings mean anything.</summary>
internal readonly record struct CpuTimes(ulong Busy, ulong Total);

/// <summary>
/// Turns two cumulative readings into a percentage. The first Next() has nothing to diff against, so it reads twice,
/// <see cref="PrimeDelay"/> apart (a one-off pause of 200 ms in the daemon, in return for a real first number rather than 0).
/// </summary>
internal sealed class CpuTracker(Func<CpuTimes?> read)
{
    public static readonly TimeSpan PrimeDelay = TimeSpan.FromMilliseconds(200);

    private CpuTimes? _previous;

    public double? Next()
    {
        if (_previous is null)
        {
            if (read() is not { } primer) return null;
            _previous = primer;
            Thread.Sleep(PrimeDelay);
        }

        if (read() is not { } now) return null;
        var pct = Percent(_previous.Value, now);
        _previous = now;
        return pct;
    }

    /// <summary>Busy share of the elapsed total, clamped to 0..100; a counter that went backwards or stood still reads 0.</summary>
    public static double Percent(CpuTimes before, CpuTimes now)
    {
        if (now.Total <= before.Total || now.Busy < before.Busy) return 0;
        var pct = (double)(now.Busy - before.Busy) / (now.Total - before.Total) * 100;
        return Math.Clamp(pct, 0, 100);
    }
}
