using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Metrics;

/// <summary>Total and available memory in bytes, as /proc/meminfo reports them.</summary>
internal readonly record struct MemInfo(long TotalBytes, long AvailableBytes);

/// <summary>
/// Linux CPU and memory from /proc and, inside a container that has a limit, from the cgroup (v2) files: the limit
/// then replaces the host's numbers. The first CPU reading waits <see cref="CpuTracker.PrimeDelay"/> (see CpuTracker).
/// Disks are added by <see cref="MetricsSource"/>.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxMetrics(string procStat = "/proc/stat", string memInfo = "/proc/meminfo", string cgroupDir = "/sys/fs/cgroup")
    : IMetricsSource
{
    public const string CpuMaxFile = "cpu.max";
    public const string CpuStatFile = "cpu.stat";
    public const string MemoryMaxFile = "memory.max";
    public const string MemoryCurrentFile = "memory.current";
    public const string MemoryStatFile = "memory.stat";
    private const long KiB = 1024;

    private readonly CpuTracker _hostCpu = new(() => ParseProcStat(File.ReadAllText(procStat)));
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private CpuTracker? _cgroupCpu;
    private double _cgroupCores;

    public MetricSample? Sample(DateTimeOffset now)
    {
        try
        {
            var cpu = ReadCpu();
            if (cpu is not { } pct || ReadMemory() is not { } mem) return null;
            return new MetricSample(now, Math.Round(pct, 1), mem.Used, mem.Total, []);
        }
        catch (Exception e) when (MetricFailure.IsExpected(e))
        {
            return null; // the daemon reports "no numbers" and tries again at the next sample
        }
    }

    private double? ReadCpu()
    {
        var cores = ParseCpuMax(ReadCgroup(CpuMaxFile));
        if (cores is not { } limit) return _hostCpu.Next();
        if (_cgroupCpu is null || Math.Abs(_cgroupCores - limit) > double.Epsilon)
        {
            _cgroupCores = limit;
            _cgroupCpu = new CpuTracker(() => ReadCgroupCpu(limit));
        }

        return _cgroupCpu.Next();
    }

    // Busy = the cgroup's CPU microseconds; total = wall microseconds x allowed cores since this instance started.
    private CpuTimes? ReadCgroupCpu(double cores)
    {
        if (ParseCpuStatUsage(ReadCgroup(CpuStatFile)) is not { } usage) return null;
        return new CpuTimes((ulong)usage, (ulong)(_clock.Elapsed.TotalMicroseconds * cores));
    }

    private (long Used, long Total)? ReadMemory()
    {
        if (ParseMemInfo(File.ReadAllText(memInfo)) is not { } host) return null;
        var hostUsed = host.TotalBytes - host.AvailableBytes;
        if (ParseCgroupLimit(ReadCgroup(MemoryMaxFile)) is not { } limit || limit >= host.TotalBytes) return (hostUsed, host.TotalBytes);
        if (ParseCgroupLimit(ReadCgroup(MemoryCurrentFile)) is not { } current) return (hostUsed, host.TotalBytes);
        var cache = ParseInactiveFile(ReadCgroup(MemoryStatFile)) ?? 0;
        return (Math.Max(0, current - cache), limit);
    }

    private string? ReadCgroup(string file)
    {
        var path = Path.Combine(cgroupDir, file);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>The first "cpu" line of /proc/stat: busy = everything but idle and iowait (guest time is already in user).</summary>
    internal static CpuTimes? ParseProcStat(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || parts[0] != "cpu") continue;
            ulong total = 0;
            ulong idle = 0;
            for (var i = 1; i < Math.Min(parts.Length, 9); i++)
            {
                var v = ulong.Parse(parts[i], CultureInfo.InvariantCulture);
                total += v;
                if (i is 4 or 5) idle += v;
            }

            return new CpuTimes(total - idle, total);
        }

        return null;
    }

    /// <summary>MemTotal and MemAvailable (kB in the file); null when either is missing.</summary>
    internal static MemInfo? ParseMemInfo(string text)
    {
        long? total = null;
        long? available = null;
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon];
            if (key is not ("MemTotal" or "MemAvailable")) continue;
            var number = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            var bytes = long.Parse(number, CultureInfo.InvariantCulture) * KiB;
            if (key == "MemTotal") total = bytes;
            else available = bytes;
        }

        return total is > 0 && available is { } a ? new MemInfo(total.Value, Math.Clamp(a, 0, total.Value)) : null;
    }

    /// <summary>cpu.max is "quota period" or "max period": the allowed cores (quota / period), or null when unlimited or absent.</summary>
    internal static double? ParseCpuMax(string? text)
    {
        var parts = text?.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: >= 2 } || parts[0] == "max") return null;
        var quota = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var period = double.Parse(parts[1], CultureInfo.InvariantCulture);
        return quota > 0 && period > 0 ? quota / period : null;
    }

    /// <summary>The "usage_usec" line of cpu.stat.</summary>
    internal static long? ParseCpuStatUsage(string? text) => ParseStatKey(text, "usage_usec");

    /// <summary>The "inactive_file" line of memory.stat: cache the kernel gives back, left out of "used" like container tools do.</summary>
    internal static long? ParseInactiveFile(string? text) => ParseStatKey(text, "inactive_file");

    /// <summary>A single number as memory.max / memory.current hold it; "max" and absent mean no limit (null).</summary>
    internal static long? ParseCgroupLimit(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value == "max") return null;
        return long.Parse(value, CultureInfo.InvariantCulture);
    }

    private static long? ParseStatKey(string? text, string key)
    {
        if (text is null) return null;
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == key) return long.Parse(parts[1], CultureInfo.InvariantCulture);
        }

        return null;
    }
}
