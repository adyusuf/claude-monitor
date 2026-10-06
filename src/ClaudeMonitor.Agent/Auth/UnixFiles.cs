using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClaudeMonitor.Agent.Auth;

/// <summary>
/// What .NET does not expose on Unix: the effective uid and a file's owner. Every answer is "unknown" (null) rather than
/// an exception when the platform call is missing or its result does not look right, and callers treat unknown as refuse.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static partial class UnixFiles
{
    public const UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public const UnixFileMode GroupOrOtherWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private const int StatBufferBytes = 512;
    private const int PermissionBits = 0xFFF;

    // Linux: statx(2), whose layout is the same on every architecture (struct statx: stx_uid at 20, stx_mode at 28).
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7FF;
    private const int StatxUidOffset = 20;
    private const int StatxModeOffset = 28;

    // macOS: the 64-bit-inode struct stat (st_mode at 4, st_uid at 16), the same on arm64 and x64.
    private const int MacModeOffset = 4;
    private const int MacUidOffset = 16;

    public static uint? Euid()
    {
        try
        {
            return GetEuid();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The owner uid of a file or folder, not following a link. Null when it cannot be read or when the stat layout read
    /// does not agree with the mode .NET reports for the same path (a layout this code does not know).
    /// </summary>
    public static uint? OwnerUid(string path)
    {
        try
        {
            var buffer = new byte[StatBufferBytes];
            int modeOffset, uidOffset;
            if (OperatingSystem.IsLinux())
            {
                if (Statx(AtFdCwd, path, AtSymlinkNoFollow, StatxBasicStats, ref buffer[0]) != 0) return null;
                (modeOffset, uidOffset) = (StatxModeOffset, StatxUidOffset);
            }
            else if (OperatingSystem.IsMacOS())
            {
                var code = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? LstatInode64(path, ref buffer[0]) : Lstat(path, ref buffer[0]);
                if (code != 0) return null;
                (modeOffset, uidOffset) = (MacModeOffset, MacUidOffset);
            }
            else
            {
                return null;
            }

            var mode = BitConverter.ToUInt16(buffer, modeOffset) & PermissionBits;
            return mode == ((int)File.GetUnixFileMode(path) & PermissionBits) ? BitConverter.ToUInt32(buffer, uidOffset) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEuid();

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int dirfd, string path, int flags, uint mask, ref byte buffer);

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Lstat(string path, ref byte buffer);

    // x64 macOS keeps the older 32-bit-inode struct under the plain name; this entry point is the current one.
    [LibraryImport("libc", EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LstatInode64(string path, ref byte buffer);
}
