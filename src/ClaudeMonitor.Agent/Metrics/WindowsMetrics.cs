using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Metrics;

/// <summary>
/// Windows CPU from GetSystemTimes (kernel time includes idle time) and memory from GlobalMemoryStatusEx.
/// Disks are added by <see cref="MetricsSource"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsMetrics : IMetricsSource
{
    private const string Kernel32 = "kernel32.dll";

    private readonly CpuTracker _cpu = new(ReadCpuTimes);

    public MetricSample? Sample(DateTimeOffset now)
    {
        try
        {
            if (_cpu.Next() is not { } pct || ReadMemory() is not { } mem) return null;
            return new MetricSample(now, Math.Round(pct, 1), mem.Used, mem.Total, []);
        }
        catch (Exception e) when (MetricFailure.IsExpected(e))
        {
            return null; // the daemon reports "no numbers" and tries again at the next sample
        }
    }

    // FILETIME is two 32-bit halves of a 64-bit count of 100 ns ticks, so a ulong receives it as is.
    private static CpuTimes? ReadCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var total = kernel + user;
        return new CpuTimes(total - Math.Min(idle, total), total);
    }

    private static (long Used, long Total)? ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return null;
        var total = (long)status.TotalPhys;
        return (Math.Max(0, total - (long)status.AvailPhys), total);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport(Kernel32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [LibraryImport(Kernel32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
}
