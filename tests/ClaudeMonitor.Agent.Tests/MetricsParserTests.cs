using System.Runtime.Versioning;
using ClaudeMonitor.Agent.Metrics;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

// The Linux parsers are pure text functions that run anywhere; the attribute only satisfies the platform analyzer.
[SupportedOSPlatform("linux")]
public sealed class MetricsParserTests
{
    private const string ProcStat = """
        cpu  100 20 30 400 50 6 7 8 90 10
        cpu0 1 2 3 4 5 6 7 8 9 10
        intr 1234
        """;

    [Fact]
    public void The_first_cpu_line_of_proc_stat_gives_busy_as_everything_but_idle_and_iowait()
    {
        var times = LinuxMetrics.ParseProcStat(ProcStat)!.Value;
        // user..steal (8 fields) = 100+20+30+400+50+6+7+8 = 621; idle+iowait = 450; guest columns are not added.
        Assert.Equal((621UL, 621UL - 450UL), (times.Total, times.Busy));
    }

    [Fact]
    public void Proc_stat_without_an_aggregate_cpu_line_is_nothing()
    {
        Assert.Null(LinuxMetrics.ParseProcStat("cpu0 1 2 3 4 5\nintr 1\n"));
        Assert.Null(LinuxMetrics.ParseProcStat("cpu 1 2 3\n"));
        Assert.Null(LinuxMetrics.ParseProcStat(""));
    }

    [Fact]
    public void Meminfo_is_read_in_bytes_and_available_is_clamped_to_the_total()
    {
        var info = LinuxMetrics.ParseMemInfo("MemTotal:       16384 kB\nMemFree:  1 kB\nMemAvailable:    4096 kB\n")!.Value;
        Assert.Equal((16384L * 1024, 4096L * 1024), (info.TotalBytes, info.AvailableBytes));

        var clamped = LinuxMetrics.ParseMemInfo("MemTotal: 100 kB\nMemAvailable: 900 kB\n")!.Value;
        Assert.Equal(100L * 1024, clamped.AvailableBytes);
    }

    [Fact]
    public void Meminfo_without_available_or_with_a_zero_total_is_nothing()
    {
        Assert.Null(LinuxMetrics.ParseMemInfo("MemTotal: 16384 kB\nMemFree: 1 kB\n"));
        Assert.Null(LinuxMetrics.ParseMemInfo("MemAvailable: 16384 kB\n"));
        Assert.Null(LinuxMetrics.ParseMemInfo("MemTotal: 0 kB\nMemAvailable: 0 kB\n"));
    }

    [Theory]
    [InlineData("200000 100000\n", 2.0)]
    [InlineData("50000 100000", 0.5)]
    public void Cpu_max_gives_the_allowed_cores_as_quota_over_period(string text, double cores) =>
        Assert.Equal(cores, LinuxMetrics.ParseCpuMax(text));

    [Theory]
    [InlineData("max 100000\n")]
    [InlineData("")]
    [InlineData("100000")]
    [InlineData("0 100000")]
    [InlineData(null)]
    public void Cpu_max_without_a_limit_is_nothing(string? text) => Assert.Null(LinuxMetrics.ParseCpuMax(text));

    [Theory]
    [InlineData("536870912\n", 536870912L)]
    [InlineData("0", 0L)]
    public void A_cgroup_number_is_read_as_it_is(string text, long value) => Assert.Equal(value, LinuxMetrics.ParseCgroupLimit(text));

    [Theory]
    [InlineData("max\n")]
    [InlineData("  ")]
    [InlineData("")]
    [InlineData(null)]
    public void A_cgroup_limit_of_max_or_absent_is_no_limit(string? text) => Assert.Null(LinuxMetrics.ParseCgroupLimit(text));

    [Fact]
    public void Cgroup_stat_files_are_read_by_key()
    {
        Assert.Equal(777L, LinuxMetrics.ParseCpuStatUsage("user_usec 1\nusage_usec 777\nsystem_usec 2\n"));
        Assert.Null(LinuxMetrics.ParseCpuStatUsage("user_usec 1\n"));
        Assert.Equal(42L, LinuxMetrics.ParseInactiveFile("anon 9\ninactive_file 42\n"));
        Assert.Null(LinuxMetrics.ParseInactiveFile(null));
    }

    [Theory]
    [InlineData("/proc", "proc")]
    [InlineData("/sys", "sysfs")]
    [InlineData("/run", "tmpfs")]
    [InlineData("/dev", "devtmpfs")]
    [InlineData("/", "overlay")]
    [InlineData("/snap/core", "squashfs")]
    [InlineData("/net", "autofs")]
    [InlineData("/dev", "devfs")]
    [InlineData("/x", "nsfs")]
    [InlineData("/sys/fs/cgroup", "cgroup2")]
    [InlineData("/sys/fs/cgroup", "CGROUP")]
    [InlineData("/run", "TMPFS")]
    public void Pseudo_file_systems_are_not_reported(string mount, string format) => Assert.False(DiskMetrics.IsReportable(mount, format));

    [Theory]
    [InlineData("/", "ext4")]
    [InlineData("/", "apfs")]
    [InlineData("C:\\", "NTFS")]
    [InlineData("/System/Volumes/Data", "apfs")]
    [InlineData("/System/Volumes/Data/", "apfs")]
    public void Real_file_systems_and_the_mac_data_volume_are_reported(string mount, string format) => Assert.True(DiskMetrics.IsReportable(mount, format));

    [Theory]
    [InlineData("/System/Volumes/VM")]
    [InlineData("/System/Volumes/Preboot")]
    [InlineData("/System/Volumes/Data/home")]
    [InlineData("/System/Volumes/Update/mnt1")]
    public void Mac_system_volumes_other_than_data_are_not_reported(string mount) => Assert.False(DiskMetrics.IsReportable(mount, "apfs"));

    [Fact]
    public void A_mount_name_loses_control_characters_and_is_cut_to_200_characters()
    {
        Assert.Equal("/mnt/evil ignore", DiskMetrics.CleanMount("/mnt/ev\nil\u0000 ig\u001b[nore\u007f".Replace("[", "", StringComparison.Ordinal)));
        Assert.Equal("/ok", DiskMetrics.CleanMount("/ok"));
        var cut = DiskMetrics.CleanMount(new string('a', 150) + "\n" + new string('b', 150));
        Assert.Equal(200, cut.Length);
        Assert.Equal(new string('a', 150) + new string('b', 50), cut);
        Assert.Equal(200, DiskMetrics.CleanMount(new string('x', 200)).Length);
    }

    [Fact]
    public void Read_never_returns_more_than_the_disk_cap_and_only_sane_numbers()
    {
        var disks = DiskMetrics.Read();
        Assert.True(disks.Count <= DiskMetrics.MaxDisks);
        Assert.All(disks, d =>
        {
            Assert.True(d.TotalBytes > 0);
            Assert.InRange(d.UsedBytes, 0, d.TotalBytes);
            Assert.True(d.Mount.Length is > 0 and <= DiskMetrics.MaxMountChars);
        });
    }

    [Fact]
    public void The_cpu_percentage_is_the_busy_share_of_the_elapsed_total_and_a_counter_going_backwards_reads_zero()
    {
        Assert.Equal(25d, CpuTracker.Percent(new CpuTimes(100, 400), new CpuTimes(150, 600)));
        Assert.Equal(0d, CpuTracker.Percent(new CpuTimes(100, 400), new CpuTimes(100, 400)));
        Assert.Equal(0d, CpuTracker.Percent(new CpuTimes(100, 400), new CpuTimes(90, 500)));
        Assert.Equal(100d, CpuTracker.Percent(new CpuTimes(0, 100), new CpuTimes(500, 200)));
    }

    [Fact]
    public void On_a_mac_the_source_gives_plausible_cpu_and_memory_and_a_source_for_another_os_gives_none()
    {
        Assert.Null(MetricsSource.For("plan9").Sample(DateTimeOffset.UtcNow));
        if (!OperatingSystem.IsMacOS()) return;

        var now = DateTimeOffset.UtcNow;
        var sample = MetricsSource.For(OsKinds.MacOs).Sample(now)!;
        Assert.Equal(now, sample.SampledAt);
        Assert.InRange(sample.CpuPct, 0, 100);
        Assert.True(sample.MemTotalBytes > 0);
        Assert.InRange(sample.MemUsedBytes, 1, sample.MemTotalBytes);
        Assert.All(sample.Disks, d => Assert.InRange(d.UsedBytes, 0, d.TotalBytes));
    }

    [Fact]
    public void A_linux_source_reads_its_files_and_a_missing_file_gives_no_sample()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var home = new TempHome();
        var stat = Path.Combine(home.Dir, "stat");
        var mem = Path.Combine(home.Dir, "meminfo");
        File.WriteAllText(stat, ProcStat);
        File.WriteAllText(mem, "MemTotal: 1000 kB\nMemAvailable: 250 kB\n");
        var sample = new LinuxMetrics(stat, mem, home.Dir).Sample(DateTimeOffset.UtcNow)!;
        Assert.Equal((1000L * 1024, 750L * 1024), (sample.MemTotalBytes, sample.MemUsedBytes));
        Assert.Null(new LinuxMetrics(Path.Combine(home.Dir, "none"), mem, home.Dir).Sample(DateTimeOffset.UtcNow));
    }
}
