using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Podium.Agent.Access;

// The Win32 half of PathIdentity: a handle opened for no access (a folder needs backup semantics), its file information (volume serial, file index,
// link count) and its final path. Links and junctions are followed, as the access rules judge where a path really leads. Compiles everywhere; runs only
// on a Windows host, and has not yet run on a real Windows Server (ADR-0005).
internal static unsafe partial class PathIdentity
{
    private const string Kernel32 = "kernel32.dll";
    private const uint ShareAll = 0x1 | 0x2 | 0x4; // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000; // FILE_FLAG_BACKUP_SEMANTICS: lets a folder be opened
    private const uint NameNormalizedDos = 0; // FILE_NAME_NORMALIZED | VOLUME_NAME_DOS
    private const int MaxFinalPath = 32_768;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh; // three FILETIMEs, 4-byte aligned
        public uint VolumeSerial;
        public uint SizeHigh, SizeLow;
        public uint Links;
        public uint IndexHigh, IndexLow;
    }

    private static ByHandleFileInformation? WindowsInfo(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var handle = Open(path);
        if (handle is null) return null;
        return GetFileInformationByHandle(handle, out var info) ? info : null;
    }

    private static string? WindowsFinalPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var handle = Open(path);
        if (handle is null) return null;
        var buffer = new char[MaxFinalPath];
        fixed (char* p = buffer)
        {
            var length = GetFinalPathNameByHandle(handle, p, (uint)buffer.Length, NameNormalizedDos);
            return length is > 0 and < MaxFinalPath ? new string(buffer, 0, (int)length) : null;
        }
    }

    private static SafeFileHandle? Open(string path)
    {
        var handle = CreateFile(path, 0, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        return null;
    }

    [LibraryImport(Kernel32, EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [LibraryImport(Kernel32, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* buffer, uint length, uint flags);
}
