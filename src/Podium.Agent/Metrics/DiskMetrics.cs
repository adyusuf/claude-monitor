using System.Text;
using Podium.Contracts;

namespace Podium.Agent.Metrics;

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

    // Volumes macOS mounts that are not the user's storage: an iOS Simulator runtime is a read-only disk image that is full by design (it showed 97 %
    // on a half-empty Mac), and the Recovery volume only repeats the numbers of the container it lives in.
    private static readonly string[] HiddenFolders = ["/Library/Developer/CoreSimulator", "/Volumes/Recovery"];

    private const string ApfsFormat = "apfs";
    private const string RootMount = "/";

    public static IReadOnlyList<DiskSample> Read()
    {
        var disks = new List<(DiskSample Disk, string Format)>();
        IEnumerable<DriveInfo> drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception e) when (MetricFailure.IsExpected(e))
        {
            return []; // no drive list: the sample goes out without disks
        }

        foreach (var drive in drives)
        {
            if (disks.Count >= MaxDisks) break;
            if (ReadDrive(drive) is { } disk) disks.Add(disk);
        }

        return Collapse(disks);
    }

    /// <summary>
    /// On a Mac the system volume ("/") and the Data volume share one APFS container, so both report the same numbers: when the Data volume is
    /// there, "/" is its repeat and is left out. Any other disk is kept.
    /// </summary>
    internal static IReadOnlyList<DiskSample> Collapse(IReadOnlyList<(DiskSample Disk, string Format)> disks)
    {
        var shared = disks.Any(d => d.Disk.Mount.TrimEnd('/') == MacDataVolume);
        return [.. disks.Where(d => !(shared && d.Disk.Mount == RootMount && d.Format.Equals(ApfsFormat, StringComparison.OrdinalIgnoreCase))).Select(d => d.Disk)];
    }

    private static (DiskSample Disk, string Format)? ReadDrive(DriveInfo drive)
    {
        try
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) return null;
            var mount = CleanMount(drive.Name);
            if (mount.Length == 0 || !IsReportable(drive.Name, drive.DriveFormat)) return null;
            var total = drive.TotalSize;
            var free = drive.TotalFreeSpace;
            return total > 0 ? (new DiskSample(mount, Math.Clamp(total - free, 0, total), total), drive.DriveFormat) : null;
        }
        catch (Exception e) when (MetricFailure.IsExpected(e))
        {
            return null; // a drive that fails is skipped; the others still report
        }
    }

    /// <summary>False for pseudo file systems (by format), for macOS system volumes other than Data and for the volumes macOS mounts for itself (by mount).</summary>
    public static bool IsReportable(string mount, string format)
    {
        if (PseudoFileSystems.Contains(format) || format.StartsWith(CgroupPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (HiddenFolders.Any(folder => IsUnder(mount, folder))) return false;
        return !mount.StartsWith(MacVolumes, StringComparison.Ordinal) || mount.TrimEnd('/') == MacDataVolume;
    }

    /// <summary>True when the mount is the folder or inside it (a whole path part: "/Volumes/Recovery 2" is not under "/Volumes/Recovery").</summary>
    private static bool IsUnder(string mount, string folder)
    {
        var clean = mount.TrimEnd('/');
        return clean == folder || clean.StartsWith(folder + "/", StringComparison.Ordinal);
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
