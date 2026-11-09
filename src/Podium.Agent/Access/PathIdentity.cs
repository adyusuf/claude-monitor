using Podium.Agent.Exec;
using Podium.Contracts;

namespace Podium.Agent.Access;

/// <summary>What a file or folder is, whatever path names it: (device, inode) on Linux and macOS, (volume serial, file index) on Windows.</summary>
internal readonly record struct FileId(ulong Volume, ulong Index);

/// <summary>
/// Real paths and file identities for the access layers (ADR-0020 §4). The path is resolved component by component, every link on the way followed
/// (<see cref="ExecPaths.RealPath"/>); on Windows the existing part is then replaced by the final path the file system gives for its handle (short names,
/// case and junctions resolved), and a network (UNC) or device path is refused. Two paths that differ as text but are one folder (a bind mount, a macOS
/// firmlink, a case or Unicode form difference) have one <see cref="FileId"/>, which is how a folder is matched beyond its spelling. Anything that cannot
/// be read is null, and the caller refuses what it cannot judge.
/// </summary>
internal static partial class PathIdentity
{
    private const string DevicePrefix = @"\\?\";
    private const string UncPrefix = @"\\?\UNC\";

    /// <summary>The real path, or null when it is not absolute, cannot be resolved or (on Windows) is a network or device path.</summary>
    public static string? Resolve(string? path, string os, bool allowMissing)
    {
        if (string.IsNullOrEmpty(path) || !OsKinds.All.Contains(os) || path.Contains('\0', StringComparison.Ordinal)) return null;
        if (os == OsKinds.Windows && IsRefusedWindowsForm(path)) return null;
        var real = ExecPaths.RealPath(path, os, allowMissing);
        if (real is null || os != OsKinds.Windows || !OperatingSystem.IsWindows()) return real;

        // The final path of the deepest part that exists, then the part that does not exist yet as written (no link can be in it).
        var existing = DeepestExisting(real);
        if (existing is null) return null;
        var final = FinalPath(existing);
        return final is null ? null : final + real[existing.Length..];
    }

    /// <summary>"\\server\share", "\\?\..." and "\\.\..." name a network share or a device, never a local folder an access rule can hold.</summary>
    public static bool IsRefusedWindowsForm(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal);

    /// <summary>The path itself when it exists, else its nearest existing ancestor; null when not even the root exists.</summary>
    public static string? DeepestExisting(string realPath)
    {
        for (var path = realPath; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
        {
            if (Directory.Exists(path) || File.Exists(path)) return path;
        }

        return null;
    }

    /// <summary>The deepest existing part of the path and every folder above it, deepest first: what a missing path is judged by.</summary>
    public static List<string> Ancestors(string realPath)
    {
        var list = new List<string>();
        for (var path = DeepestExisting(realPath); !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path)) list.Add(path);
        return list;
    }

    /// <summary>The identity of an existing file or folder (links followed), or null when it cannot be read on this host.</summary>
    public static FileId? Of(string path)
    {
        if (OperatingSystem.IsWindows()) return WindowsInfo(path) is { } info ? new FileId(info.VolumeSerial, ((ulong)info.IndexHigh << 32) | info.IndexLow) : null;
        return UnixFileInfo.TryGet(path) is { } stat && (stat.Device | stat.Inode) != 0 ? new FileId(stat.Device, stat.Inode) : null;
    }

    /// <summary>How many hard links the file has, or null when that cannot be read on this host.</summary>
    public static ulong? LinkCount(string path)
    {
        if (OperatingSystem.IsWindows()) return WindowsInfo(path)?.Links;
        return UnixFileInfo.TryGet(path)?.LinkCount;
    }

    /// <summary>The handle's final path in DOS form ("C:\..."), or null for a network or volume-only path or when the handle cannot be opened.</summary>
    private static string? FinalPath(string existing)
    {
        if (!OperatingSystem.IsWindows()) return existing;
        var final = WindowsFinalPath(existing);
        if (final is null || final.StartsWith(UncPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var dos = final.StartsWith(DevicePrefix, StringComparison.Ordinal) ? final[DevicePrefix.Length..] : final;
        return dos.Length >= 3 && char.IsAsciiLetter(dos[0]) && dos[1] == ':' && dos[2] == '\\' ? dos : null;
    }
}
