using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>
/// macOS: the process table through libproc (public headers: libproc.h, sys/proc_info.h). A pid's identity is its pid AND
/// its start time, so that a recycled pid is never taken for the process that held it before.
/// </summary>
internal sealed unsafe partial class LibProcTable : IProcessTable
{
    private const string LibProc = "libproc";
    private const uint PidsByParent = 6; // PROC_PPID_ONLY
    private const int FlavorBsdInfo = 3; // PROC_PIDTBSDINFO
    private const int Esrch = 3;
    private const int Eperm = 1;
    private const int PidBytes = sizeof(int);
    private const int NewChildrenSlack = 16; // forks between the size query and the read

    // struct proc_bsdinfo: 136 bytes, fixed by the header.
    private const int BsdInfoBytes = 136;
    private const int ParentPidOffset = 16;
    private const int GroupIdOffset = 100;
    private const int StartSecondsOffset = 120;
    private const int StartMicrosOffset = 128;
    private const long MicrosPerSecond = 1_000_000;

    public ProcessStamp? Find(int pid)
    {
        Span<byte> info = stackalloc byte[BsdInfoBytes];
        int read;
        fixed (byte* p = info)
        {
            read = proc_pidinfo(pid, FlavorBsdInfo, 0, p, BsdInfoBytes);
        }

        if (read == 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno is Esrch or Eperm) return null; // gone, or not ours to see
            throw new IOException($"proc_pidinfo failed (errno {errno})");
        }

        if (read != BsdInfoBytes) throw new IOException("proc_pidinfo returned an unexpected size.");
        var seconds = (long)BinaryPrimitives.ReadUInt64LittleEndian(info[StartSecondsOffset..]);
        var micros = (long)BinaryPrimitives.ReadUInt64LittleEndian(info[StartMicrosOffset..]);
        return new ProcessStamp(pid, (seconds * MicrosPerSecond) + micros, (int)BinaryPrimitives.ReadUInt32LittleEndian(info[GroupIdOffset..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(info[ParentPidOffset..]));
    }

    public IReadOnlyList<ProcessStamp> ChildrenOf(int pid)
    {
        var bytes = proc_listpids(PidsByParent, (uint)pid, null, 0);
        if (bytes < 0) throw new IOException($"proc_listpids failed (errno {Marshal.GetLastPInvokeError()})");
        if (bytes == 0) return [];

        var pids = new int[(bytes / PidBytes) + NewChildrenSlack];
        int filled;
        fixed (int* p = pids)
        {
            filled = proc_listpids(PidsByParent, (uint)pid, p, pids.Length * PidBytes);
        }

        if (filled < 0) throw new IOException($"proc_listpids failed (errno {Marshal.GetLastPInvokeError()})");
        var children = new List<ProcessStamp>();
        foreach (var child in pids.Take(filled / PidBytes).Where(c => c > 0))
        {
            // a child that exited since the list was made is gone; one whose parent changed in the meantime is not this one's child
            if (Find(child) is { } stamp && stamp.ParentPid == pid) children.Add(stamp);
        }

        return children;
    }

    [LibraryImport(LibProc, SetLastError = true)]
    private static partial int proc_listpids(uint type, uint typeInfo, int* buffer, int bufferSize);

    [LibraryImport(LibProc, SetLastError = true)]
    private static partial int proc_pidinfo(int pid, int flavor, ulong arg, byte* buffer, int bufferSize);
}
