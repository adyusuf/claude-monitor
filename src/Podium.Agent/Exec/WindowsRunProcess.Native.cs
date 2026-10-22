using System.Runtime.InteropServices;

namespace Podium.Agent.Exec;

/// <summary>The Win32 structures and imports <see cref="WindowsRunProcess"/> uses, as the documented contracts spell them.</summary>
internal sealed unsafe partial class WindowsRunProcess
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; // IO_COUNTERS
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo Startup;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [LibraryImport("kernel32", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial int SetInformationJobObject(nint job, int infoClass, nint info, uint length);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial int AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial int TerminateJobObject(nint job, uint exitCode);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial int TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateProcessW(string application, nint commandLine, nint processAttributes, nint threadAttributes,
        int inheritHandles, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInformation info);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial int GetExitCodeProcess(nint process, out uint code);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial int SetHandleInformation(nint handle, uint mask, uint flags);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial int InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial int UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous,
        nint returnSize);

    [LibraryImport("kernel32")]
    private static partial void DeleteProcThreadAttributeList(nint list);
}
