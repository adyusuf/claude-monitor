using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>
/// Linux and macOS: the run starts through posix_spawn in a process group of its own, so that kill(-pgid) reaches every
/// descendant that did not leave the group. A double-forked child that calls setsid() does leave it: on Linux the systemd
/// unit's KillMode=control-group still ends it; on macOS it is a residual risk (ADR-0005). The group id is the lead's pid;
/// a signal sent after the lead was reaped could in theory reach a recycled id, so the agent only does that to clean up
/// right after a run, within seconds.
/// </summary>
internal sealed unsafe partial class UnixRunProcess : RunProcessBase
{
    private const short SetPGroup = 0x02;
    private const short SetSigDef = 0x04;
    private const short SetSigMask = 0x08;
    private const short MacCloexecDefault = 0x4000;
    private const int ORdOnly = 0;
    private const int Sigterm = 15;
    private const int Sigkill = 9;
    private const int Eintr = 4;
    private const int Esrch = 3;
    private const int Eacces = 13;
    private const int XOk = 1;
    private const int FirstFreeFd = 3;
    private const int StatusExitShift = 8;
    private const int StatusByteMask = 0xff;
    private const int StatusSignalMask = 0x7f;

    // Opaque in C and different per libc (glibc 336 / 80 bytes, musl 64 / 80, macOS one pointer): generous room for all.
    private const int AttrBytes = 512;
    private const int ActionsBytes = 512;
    private const int SigsetBytes = 256;
    private const string LibC = "libc";
    private const string DevNull = "/dev/null";

    private static readonly nint AddCloseFrom = ResolveAddCloseFrom();

    private readonly int _pid;

    private UnixRunProcess(Stream stdout, Stream stderr, int pid) : base(stdout, stderr) => _pid = pid;

    public static UnixRunProcess Start(string exe, IReadOnlyList<string> argv, string cwd, IReadOnlyDictionary<string, string> env)
    {
        // .NET creates both pipes close-on-exec, so no other child can inherit them; the spawn dup2s the write ends to 1 and 2.
        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        try
        {
            var pid = Spawn(exe, argv, cwd, env, FdOf(stdout.ClientSafePipeHandle), FdOf(stderr.ClientSafePipeHandle));
            stdout.DisposeLocalCopyOfClientHandle(); // else the read end never sees the end of the output
            stderr.DisposeLocalCopyOfClientHandle();
            var process = new UnixRunProcess(stdout, stderr, pid);
            process.BeginWait();
            return process;
        }
        catch
        {
            stdout.Dispose();
            stderr.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Fails like posix_spawn would when exe is missing or not executable. A run that starts through the launcher
    /// (<see cref="RunLauncher"/>) would otherwise only show an exit code of 126 or 127 from the shell.
    /// </summary>
    public static void RequireExecutable(string exe)
    {
        if (Directory.Exists(exe)) throw new Win32Exception(Eacces, "Could not start the run (exec).");
        if (access(exe, XOk) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not start the run (exec).");
    }

    public override void Kill() => SendToGroup(Sigterm);

    public override void KillNow() => SendToGroup(Sigkill);

    protected override RunExit WaitForExit()
    {
        while (true)
        {
            if (waitpid(_pid, out var status, 0) == _pid) return Decode(status);
            var errno = Marshal.GetLastPInvokeError();
            if (errno != Eintr) throw new IOException($"waitpid failed (errno {errno})");
        }
    }

    internal static RunExit Decode(int status)
    {
        var signal = status & StatusSignalMask;
        return signal == 0 ? new RunExit((status >> StatusExitShift) & StatusByteMask, null) : new RunExit(128 + signal, signal);
    }

    private void SendToGroup(int signal)
    {
        if (kill(-_pid, signal) == 0) return;
        var errno = Marshal.GetLastPInvokeError();
        if (errno != Esrch) throw new IOException($"kill failed (errno {errno})"); // ESRCH: the group is already gone
    }

    private static int FdOf(SafePipeHandle handle)
    {
        var fd = (int)handle.DangerousGetHandle();
        if (fd < FirstFreeFd) throw new InvalidOperationException("The agent's own standard streams are closed.");
        return fd;
    }

    private static int Spawn(string exe, IReadOnlyList<string> argv, string cwd, IReadOnlyDictionary<string, string> env, int outFd, int errFd)
    {
        var argvPtrs = new nint[argv.Count + 1];
        var envPtrs = new nint[env.Count + 1];
        nint attr = 0, actions = 0, mask = 0, defaults = 0;
        bool attrReady = false, actionsReady = false;
        try
        {
            for (var i = 0; i < argv.Count; i++) argvPtrs[i] = Marshal.StringToCoTaskMemUTF8(argv[i]);
            var e = 0;
            foreach (var (name, value) in env) envPtrs[e++] = Marshal.StringToCoTaskMemUTF8($"{name}={value}");

            attr = (nint)NativeMemory.AllocZeroed(AttrBytes);
            actions = (nint)NativeMemory.AllocZeroed(ActionsBytes);
            mask = (nint)NativeMemory.AllocZeroed(SigsetBytes);
            defaults = (nint)NativeMemory.AllocZeroed(SigsetBytes);
            Check(sigemptyset(mask), "sigemptyset");
            Check(sigfillset(defaults), "sigfillset");

            Check(posix_spawnattr_init(attr), "posix_spawnattr_init");
            attrReady = true;
            var flags = (short)(SetPGroup | SetSigDef | SetSigMask);
            if (OperatingSystem.IsMacOS()) flags |= MacCloexecDefault; // everything not named below is closed in the child
            Check(posix_spawnattr_setflags(attr, flags), "setflags");
            Check(posix_spawnattr_setpgroup(attr, 0), "setpgroup"); // 0: the child's pgid is its own pid
            Check(posix_spawnattr_setsigmask(attr, mask), "setsigmask");
            Check(posix_spawnattr_setsigdefault(attr, defaults), "setsigdefault");

            Check(posix_spawn_file_actions_init(actions), "file_actions_init");
            actionsReady = true;
            Check(posix_spawn_file_actions_addopen(actions, 0, DevNull, ORdOnly, 0), "stdin");
            Check(posix_spawn_file_actions_adddup2(actions, outFd, 1), "stdout");
            Check(posix_spawn_file_actions_adddup2(actions, errFd, 2), "stderr");
            if (AddCloseFrom != 0) Check(((delegate* unmanaged<nint, int, int>)AddCloseFrom)(actions, FirstFreeFd), "closefrom");
            Check(posix_spawn_file_actions_addchdir_np(actions, cwd), "chdir");

            fixed (nint* argvP = argvPtrs)
            fixed (nint* envP = envPtrs)
            {
                Check(posix_spawn(out var pid, exe, actions, attr, argvP, envP), "spawn");
                return pid;
            }
        }
        finally
        {
            // destroy only frees memory; there is nothing to do when it reports an error
            if (actionsReady) _ = posix_spawn_file_actions_destroy(actions);
            if (attrReady) _ = posix_spawnattr_destroy(attr);
            NativeMemory.Free((void*)attr);
            NativeMemory.Free((void*)actions);
            NativeMemory.Free((void*)mask);
            NativeMemory.Free((void*)defaults);
            foreach (var p in argvPtrs) Marshal.FreeCoTaskMem(p);
            foreach (var p in envPtrs) Marshal.FreeCoTaskMem(p);
        }
    }

    // The libc calls return the errno itself, not -1.
    private static void Check(int errno, string what)
    {
        if (errno != 0) throw new Win32Exception(errno, $"Could not start the run ({what}).");
    }

    // glibc 2.34+ only; elsewhere the descriptors are close-on-exec already (.NET opens everything that way).
    private static nint ResolveAddCloseFrom()
    {
        if (!OperatingSystem.IsLinux()) return 0;
        return NativeLibrary.TryLoad("libc.so.6", out var libc)
            && NativeLibrary.TryGetExport(libc, "posix_spawn_file_actions_addclosefrom_np", out var fn) ? fn : 0;
    }

    [LibraryImport(LibC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int posix_spawn(out int pid, string path, nint actions, nint attr, nint* argv, nint* envp);

    [LibraryImport(LibC)]
    private static partial int posix_spawnattr_init(nint attr);

    [LibraryImport(LibC)]
    private static partial int posix_spawnattr_destroy(nint attr);

    [LibraryImport(LibC)]
    private static partial int posix_spawnattr_setflags(nint attr, short flags);

    [LibraryImport(LibC)]
    private static partial int posix_spawnattr_setpgroup(nint attr, int pgroup);

    [LibraryImport(LibC)]
    private static partial int posix_spawnattr_setsigmask(nint attr, nint sigset);

    [LibraryImport(LibC)]
    private static partial int posix_spawnattr_setsigdefault(nint attr, nint sigset);

    [LibraryImport(LibC)]
    private static partial int posix_spawn_file_actions_init(nint actions);

    [LibraryImport(LibC)]
    private static partial int posix_spawn_file_actions_destroy(nint actions);

    [LibraryImport(LibC)]
    private static partial int posix_spawn_file_actions_adddup2(nint actions, int fd, int newFd);

    [LibraryImport(LibC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int posix_spawn_file_actions_addopen(nint actions, int fd, string path, int oflag, uint mode);

    [LibraryImport(LibC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int posix_spawn_file_actions_addchdir_np(nint actions, string path);

    [LibraryImport(LibC)]
    private static partial int sigemptyset(nint set);

    [LibraryImport(LibC)]
    private static partial int sigfillset(nint set);

    [LibraryImport(LibC, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int access(string path, int mode);

    [LibraryImport(LibC, SetLastError = true)]
    private static partial int kill(int pid, int signal);

    [LibraryImport(LibC, SetLastError = true)]
    private static partial int waitpid(int pid, out int status, int options);
}
