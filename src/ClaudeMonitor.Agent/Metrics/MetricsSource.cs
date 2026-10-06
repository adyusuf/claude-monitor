using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Metrics;

/// <summary>Chooses the metrics source of an OS (an <see cref="OsKinds"/> value) and adds the disks to its samples.</summary>
public static class MetricsSource
{
    public static IMetricsSource For(string os) => os switch
    {
        OsKinds.Linux when OperatingSystem.IsLinux() => new WithDisks(new LinuxMetrics()),
        OsKinds.MacOs when OperatingSystem.IsMacOS() => new WithDisks(new MacMetrics()),
        OsKinds.Windows when OperatingSystem.IsWindows() => new WithDisks(new WindowsMetrics()),
        _ => new Unavailable(),
    };

    /// <summary>Puts the disks into the sample of the CPU and memory source; no CPU or memory means no sample.</summary>
    internal sealed class WithDisks(IMetricsSource inner) : IMetricsSource
    {
        public MetricSample? Sample(DateTimeOffset now) => inner.Sample(now) is { } sample ? sample with { Disks = DiskMetrics.Read() } : null;
    }

    internal sealed class Unavailable : IMetricsSource
    {
        public MetricSample? Sample(DateTimeOffset now) => null;
    }
}
