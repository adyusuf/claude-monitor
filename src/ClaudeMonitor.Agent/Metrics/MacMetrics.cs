using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Metrics;

/// <summary>
/// macOS CPU from the Mach host's cumulative ticks and memory as active + wired + compressed pages (what the kernel
/// cannot hand back without paging), through libSystem. Disks are added by <see cref="MetricsSource"/>.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacMetrics : IMetricsSource
{
    private const string LibSystem = "libSystem.dylib";
    private const int HostCpuLoadInfo = 3;
    private const int HostVmInfo64 = 4;
    private const uint CpuLoadInfoCount = 4; // host_cpu_load_info: user, system, idle, nice (natural_t each)
    private const uint VmInfo64Count = 38; // sizeof(vm_statistics64) / sizeof(integer_t)
    private const int KernSuccess = 0;

    // Indexes of natural_t fields in host_cpu_load_info and vm_statistics64 (both begin with 32-bit counters).
    private const int CpuUser = 0;
    private const int CpuSystem = 1;
    private const int CpuIdle = 2;
    private const int CpuNice = 3;
    private const int VmActive = 1;
    private const int VmWired = 3;
    private const int VmCompressor = 32;

    private readonly CpuTracker _cpu = new(ReadCpuTicks);
    private long _memTotal;
    private long _pageSize;

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

    private static unsafe CpuTimes? ReadCpuTicks()
    {
        var info = stackalloc int[(int)CpuLoadInfoCount];
        var count = CpuLoadInfoCount;
        if (host_statistics(mach_host_self(), HostCpuLoadInfo, info, ref count) != KernSuccess || count < CpuLoadInfoCount) return null;
        var idle = (ulong)(uint)info[CpuIdle];
        var busy = (ulong)(uint)info[CpuUser] + (uint)info[CpuSystem] + (uint)info[CpuNice];
        return new CpuTimes(busy, busy + idle);
    }

    private unsafe (long Used, long Total)? ReadMemory()
    {
        if (_memTotal <= 0) _memTotal = SysctlValue("hw.memsize");
        if (_pageSize <= 0) _pageSize = SysctlValue("hw.pagesize");
        if (_memTotal <= 0 || _pageSize <= 0) return null;
        var info = stackalloc int[(int)VmInfo64Count];
        var count = VmInfo64Count;
        if (host_statistics64(mach_host_self(), HostVmInfo64, info, ref count) != KernSuccess || count < VmInfo64Count) return null;
        var pages = (long)(uint)info[VmActive] + (uint)info[VmWired] + (uint)info[VmCompressor];
        return (Math.Min(pages * _pageSize, _memTotal), _memTotal);
    }

    // hw.memsize is 8 bytes and hw.pagesize 4 or 8: a zeroed 8-byte buffer reads right for both (little endian).
    private static unsafe long SysctlValue(string name)
    {
        ulong value = 0;
        nuint size = sizeof(ulong);
        return sysctlbyname(name, &value, ref size, null, 0) == 0 ? (long)value : 0;
    }

    [LibraryImport(LibSystem)]
    private static partial uint mach_host_self();

    [LibraryImport(LibSystem)]
    private static unsafe partial int host_statistics(uint host, int flavor, int* info, ref uint count);

    [LibraryImport(LibSystem)]
    private static unsafe partial int host_statistics64(uint host, int flavor, int* info, ref uint count);

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int sysctlbyname(string name, void* oldp, ref nuint oldlen, void* newp, nuint newlen);
}
