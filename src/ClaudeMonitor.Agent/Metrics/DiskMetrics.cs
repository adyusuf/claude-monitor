using System.Text;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Metrics;

/// <summary>
/// Used and total bytes of the machine's fixed disks. DriveInfo has no timeout: a drive that hangs (a dead mount) would
/// block this call, which is why only fixed, ready drives are read, each inside its own guard.
/// </summary>
public static class DiskMetrics
{
    public const int MaxDisks = 16;
    public const int MaxMountChars = 200;
    private const string CgroupPrefix = "cgroup";
    private const string MacVolumes = "/System/Volumes/";
    private const string MacDataVolume = "/System/Volumes/Data";

    private static readonly HashSet<string> PseudoFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "proc", "sysfs", "tmpfs", "devtmpfs", "overlay", "squashfs", "autofs", "devfs", "nsfs",
    };

    public static IReadOnlyList<DiskSample> Read()
    {
        var disks = new List<DiskSample>();
        IEnumerable<DriveInfo> drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception e) when (MetricFailure.IsExpected(e))
        {
            return disks; // no drive list: the sample goes out without disks
        }

        foreach (var drive in drives)
        {
            if (disks.Count >= MaxDisks) break;
            if (ReadDrive(drive) is { } disk) disks.Add(disk);
        }

        return disks;
    }

    private static DiskSample? ReadDrive(DriveInfo drive)
    {
        try
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) return null;
            var mount = CleanMount(drive.Name);
            if (mount.Length == 0 || !IsReportable(drive.Name, drive.DriveFormat)) return null;
            var total = drive.TotalSize;
            var free = drive.TotalFreeSpace;
            return total > 0 ? new DiskSample(mount, Math.Clamp(total - free, 0, total), total) : null;
        }
        catch (Exception e) when (MetricFailure.IsExpected(e))
        {
            return null; // a drive that fails is skipped; the others still report
        }
    }

    /// <summary>False for pseudo file systems (by format) and for macOS system volumes other than Data (by mount).</summary>
    public static bool IsReportable(string mount, string format)
    {
        if (PseudoFileSystems.Contains(format) || format.StartsWith(CgroupPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        return !mount.StartsWith(MacVolumes, StringComparison.Ordinal) || mount.TrimEnd('/') == MacDataVolume;
    }

    /// <summary>Control characters removed, then cut to <see cref="MaxMountChars"/>: a mount name is host-controlled text.</summary>
    public static string CleanMount(string name)
    {
        var clean = new StringBuilder(Math.Min(name.Length, MaxMountChars));
        foreach (var c in name)
        {
            if (char.IsControl(c)) continue;
            if (clean.Length >= MaxMountChars) break;
            clean.Append(c);
        }

        return clean.ToString();
    }
}
