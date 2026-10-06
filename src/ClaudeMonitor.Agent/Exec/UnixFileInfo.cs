using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>The owner and link count of a file or folder, which .NET does not expose; null where libc cannot tell.</summary>
internal readonly record struct UnixFileStat(uint OwnerUid, ulong LinkCount);

/// <summary>
/// stat(2) read from a raw buffer: struct stat has no managed definition and its layout depends on the OS and the CPU.
/// Only the layouts of macOS (x64, arm64) and Linux (x64, arm64) are known; anything else, or a libc without a
/// <c>stat</c> symbol (glibc before 2.33), gives null and the caller falls back to the permission bits alone.
/// </summary>
internal static unsafe partial class UnixFileInfo
{
    private const int BufferSize = 256;
    private const int MacOsLinkCountOffset = 6;
    private const int MacOsUidOffset = 16;
    private const int LinuxX64LinkCountOffset = 16;
    private const int LinuxX64UidOffset = 28;
    private const int LinuxArm64LinkCountOffset = 20;
    private const int LinuxArm64UidOffset = 24;
    private const int MacOsModeOffset = 4;
    private const int LinuxX64ModeOffset = 24;
    private const int LinuxArm64ModeOffset = 16;
    private const int PermissionBits = 0x1FF;

    public static UnixFileStat? TryGet(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            byte* buffer = stackalloc byte[BufferSize];
            // macOS x64 exports the 64-bit-inode layout under stat$INODE64; plain stat there is the legacy layout.
            var mac64 = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
            if ((mac64 ? StatInode64(path, buffer) : Stat(path, buffer)) != 0)
            {
                return null;
            }

            // A layout guard: the permission bits read from the buffer must be the ones .NET reports, else the offsets are wrong.
            return ModeBits(buffer) is { } mode && mode == ((int)File.GetUnixFileMode(path) & PermissionBits) ? Read(buffer) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null; // libc has no stat symbol: ownership cannot be read
        }
    }

    private static UnixFileStat? Read(byte* buffer)
    {
        var arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsMacOS() && (arm64 || RuntimeInformation.ProcessArchitecture == Architecture.X64))
        {
            return new UnixFileStat(Unsafe.ReadUnaligned<uint>(buffer + MacOsUidOffset), Unsafe.ReadUnaligned<ushort>(buffer + MacOsLinkCountOffset));
        }

        if (OperatingSystem.IsLinux() && arm64)
        {
            return new UnixFileStat(Unsafe.ReadUnaligned<uint>(buffer + LinuxArm64UidOffset), Unsafe.ReadUnaligned<uint>(buffer + LinuxArm64LinkCountOffset));
        }

        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return new UnixFileStat(Unsafe.ReadUnaligned<uint>(buffer + LinuxX64UidOffset), Unsafe.ReadUnaligned<ulong>(buffer + LinuxX64LinkCountOffset));
        }

        return null;
    }

    private static int? ModeBits(byte* buffer)
    {
        var arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsMacOS()) return Unsafe.ReadUnaligned<ushort>(buffer + MacOsModeOffset) & PermissionBits;
        if (OperatingSystem.IsLinux() && arm64) return (int)(Unsafe.ReadUnaligned<uint>(buffer + LinuxArm64ModeOffset) & PermissionBits);
        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return (int)(Unsafe.ReadUnaligned<uint>(buffer + LinuxX64ModeOffset) & PermissionBits);
        }

        return null;
    }

    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Stat(string path, byte* buffer);

    [LibraryImport("libc", EntryPoint = "stat$INODE64", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatInode64(string path, byte* buffer);
}
