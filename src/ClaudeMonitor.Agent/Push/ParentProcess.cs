using System.Runtime.InteropServices;

namespace ClaudeMonitor.Agent.Push;

/// <summary>
/// The id of the process that started this one. A hook and the MCP server of one Claude Code session are both its
/// children, so they share it: that is how the MCP process finds out which session it serves (ADR-0003).
/// </summary>
public static partial class ParentProcess
{
    public static int Id()
    {
        try
        {
            return OperatingSystem.IsWindows() ? WindowsParent() : GetPpid();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return 0; // unknown: the session id in the environment is used instead
        }
    }

    [LibraryImport("libc", EntryPoint = "getppid")]
    private static partial int GetPpid();

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [LibraryImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static partial int NtQueryInformationProcess(nint process, int informationClass, ref ProcessBasicInformation info, int size, out int returned);

    private static int WindowsParent()
    {
        var info = new ProcessBasicInformation();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return NtQueryInformationProcess(self.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
            ? (int)info.InheritedFromUniqueProcessId
            : 0;
    }
}
