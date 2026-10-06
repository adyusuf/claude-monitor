using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>
/// Windows: the run lives in a Job Object created for it with kill-on-close, an active-process limit (64), a job memory
/// limit (2 GB) and no breakaway. The process is created suspended, assigned to the job and only then resumed, so it can
/// not start a child outside it. Only the three standard handles are inherited (PROC_THREAD_ATTRIBUTE_HANDLE_LIST); stdin
/// is NUL. Not verified on a Windows machine: written to the documented Win32 contracts only.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsRunProcess : RunProcessBase
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint LimitActiveProcess = 0x8;
    private const uint LimitJobMemory = 0x200;
    private const uint LimitKillOnJobClose = 0x2000;
    private const uint ActiveProcessMax = 64;
    private const ulong JobMemoryMax = 2UL * 1024 * 1024 * 1024;
    private const uint CreateSuspended = 0x4;
    private const uint CreateUnicodeEnvironment = 0x400;
    private const uint ExtendedStartupInfoPresent = 0x80000;
    private const uint CreateNoWindow = 0x08000000;
    private const int UseStdHandles = 0x100;
    private const uint HandleFlagInherit = 0x1;
    private const nuint ProcThreadAttributeHandleList = 0x00020002;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint ResumeFailed = 0xFFFFFFFF;
    private const uint KilledExitCode = 1;
    private const string NulDevice = "NUL";

    private readonly nint _job;
    private readonly nint _process;
    private int _jobClosed;

    private WindowsRunProcess(Stream stdout, Stream stderr, nint job, nint process) : base(stdout, stderr)
    {
        _job = job;
        _process = process;
    }

    public static WindowsRunProcess Start(string exe, string commandLine, string cwd, IReadOnlyDictionary<string, string> env)
    {
        var job = CreateJob();
        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        try
        {
            using var nul = File.OpenHandle(NulDevice, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var process = Create(exe, commandLine, cwd, env, nul.DangerousGetHandle(),
                stdout.ClientSafePipeHandle.DangerousGetHandle(), stderr.ClientSafePipeHandle.DangerousGetHandle(), job);
            stdout.DisposeLocalCopyOfClientHandle(); // else the read end never sees the end of the output
            stderr.DisposeLocalCopyOfClientHandle();
            var run = new WindowsRunProcess(stdout, stderr, job, process);
            run.BeginWait();
            return run;
        }
        catch
        {
            CloseHandle(job); // kill-on-close ends anything that did start
            stdout.Dispose();
            stderr.Dispose();
            throw;
        }
    }

    // Windows has nothing gentler than this for a process without a console window.
    public override void Kill() => KillNow();

    public override void KillNow()
    {
        if (Volatile.Read(ref _jobClosed) != 0) return;
        if (TerminateJobObject(_job, KilledExitCode) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    protected override RunExit WaitForExit()
    {
        var process = _process;
        try
        {
            if (WaitForSingleObject(process, Infinite) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (GetExitCodeProcess(process, out var code) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return new RunExit(unchecked((int)code), null);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    protected override void ReleaseNative()
    {
        if (Interlocked.Exchange(ref _jobClosed, 1) == 0) CloseHandle(_job); // kill-on-close ends the stragglers
    }

    private static nint CreateJob()
    {
        var job = CreateJobObjectW(0, null);
        if (job == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var info = new ExtendedLimit
        {
            Basic = { LimitFlags = LimitActiveProcess | LimitJobMemory | LimitKillOnJobClose, ActiveProcessLimit = ActiveProcessMax },
            JobMemoryLimit = (nuint)JobMemoryMax,
        };
        if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, (nint)(&info), (uint)sizeof(ExtendedLimit)) == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            CloseHandle(job);
            throw new Win32Exception(error);
        }

        return job;
    }

    private static nint Create(string exe, string commandLine, string cwd, IReadOnlyDictionary<string, string> env,
        nint stdin, nint stdout, nint stderr, nint job)
    {
        nint block = 0, line = 0;
        nint* handles = stackalloc nint[] { stdin, stdout, stderr };
        nint list = 0;
        var inherited = new[] { stdin, stdout, stderr };
        try
        {
            block = Marshal.StringToHGlobalUni(EnvironmentBlock(env));
            line = Marshal.StringToHGlobalUni(commandLine); // CreateProcessW may write into it
            foreach (var h in inherited) SetHandleInformation(h, HandleFlagInherit, HandleFlagInherit);

            nuint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size); // fails by design, answering the size
            list = (nint)NativeMemory.Alloc(size);
            if (InitializeProcThreadAttributeList(list, 1, 0, ref size) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (UpdateProcThreadAttribute(list, 0, ProcThreadAttributeHandleList, (nint)handles, (nuint)(3 * sizeof(nint)), 0, 0) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var si = new StartupInfoEx { AttributeList = list };
            si.Startup.Size = sizeof(StartupInfoEx);
            si.Startup.Flags = UseStdHandles;
            si.Startup.StdInput = stdin;
            si.Startup.StdOutput = stdout;
            si.Startup.StdError = stderr;

            const uint flags = CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent | CreateNoWindow;
            if (CreateProcessW(exe, line, 0, 0, 1, flags, block, cwd, ref si, out var pi) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not start the run.");
            }

            try
            {
                if (AssignProcessToJobObject(job, pi.Process) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                if (ResumeThread(pi.Thread) == ResumeFailed) throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            catch
            {
                TerminateProcess(pi.Process, KilledExitCode);
                CloseHandle(pi.Process);
                throw;
            }
            finally
            {
                CloseHandle(pi.Thread);
            }

            return pi.Process;
        }
        finally
        {
            foreach (var h in inherited) SetHandleInformation(h, HandleFlagInherit, 0);
            if (list != 0)
            {
                DeleteProcThreadAttributeList(list);
                NativeMemory.Free((void*)list);
            }

            Marshal.FreeHGlobal(block);
            Marshal.FreeHGlobal(line);
        }
    }

    // KEY=VALUE\0 ... \0\0, sorted by name without regard to case as CreateProcess documents.
    private static string EnvironmentBlock(IReadOnlyDictionary<string, string> env)
    {
        var sb = new StringBuilder();
        foreach (var (name, value) in env.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) sb.Append(name).Append('=').Append(value).Append('\0');
        return sb.Append('\0').ToString();
    }

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
