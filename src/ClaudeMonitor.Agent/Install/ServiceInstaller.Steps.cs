using System.Diagnostics;
using System.Globalization;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Install;

/// <summary>The steps of <see cref="ServiceInstaller"/>: accounts, files, folders and the undo of what a failed run created.</summary>
public sealed partial class ServiceInstaller
{
    private const UnixFileMode Mode755 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
        | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode Mode644 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode Mode700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>The default for the <c>run</c> delegate: starts the tool without a shell, with the caller's terminal; a missing tool is 127.</summary>
    public static int RunTool(string tool, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            var info = new ProcessStartInfo(tool) { UseShellExecute = false };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ToolNotFound;
        }
    }

    /// <summary>Standard output of a tool, or null when it is missing or failed.</summary>
    public static string? CaptureTool(string tool, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            var info = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? text : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private void InstallUnix(ServiceLayout l, string currentBinary, string execLevel)
    {
        if (os == OsKinds.Linux) EnsureLinuxAccount(l);
        else EnsureMacAccount(l);
        var upgrade = File.Exists(Definition());
        InstallBinary(l, currentBinary);
        PrepareHome(l);
        WritePolicy(l, execLevel);
        if (os == OsKinds.Linux)
        {
            undo.Add(("systemd reloaded", () => Step("reload systemd", Systemctl, ["daemon-reload"]))); // runs after the unit is deleted
            WriteDefinition(SystemdUnit.Render(l));
            Step("reload systemd", Systemctl, ["daemon-reload"]);
            undo.Add(("service enabled", () => Step("disable the service", Systemctl, ["disable", "--now", ServiceLayout.ServiceName])));
            Step("enable and start the service", Systemctl, ["enable", "--now", ServiceLayout.ServiceName]);
            if (upgrade) Step("restart the service on the new binary", Systemctl, ["restart", ServiceLayout.ServiceName]);
            return;
        }

        if (upgrade) run(Launchctl, ["bootout", $"system/{LaunchdDaemon.Label}"]); // the old copy may not be loaded
        WriteDefinition(LaunchdDaemon.Render(l));
        undo.Add(("service loaded", () => Step("unload the service", Launchctl, ["bootout", $"system/{LaunchdDaemon.Label}"])));
        Step("load the service", Launchctl, ["bootstrap", "system", Definition()]);
    }

    /// <summary>Windows: the virtual account exists once the service does, so the ACLs come after sc create; a running old copy is stopped first.</summary>
    private void InstallWindows(ServiceLayout l, string currentBinary, string execLevel)
    {
        if (run(Sc(), WindowsServiceSetup.Query()) == 0)
        {
            run(Sc(), WindowsServiceSetup.Stop());
            WaitStopped(); // sc stop returns at once; the binary stays locked and delete is deferred until the process ends
            Step("remove the old service", Sc(), WindowsServiceSetup.Delete());
        }

        InstallBinary(l, currentBinary);
        PrepareHome(l);
        Step("create the service", Sc(), WindowsServiceSetup.Create(l));
        undo.Add(("service created", () => Step("remove the service", Sc(), WindowsServiceSetup.Delete())));
        Step("describe the service", Sc(), WindowsServiceSetup.Describe());
        Step("set the restart policy", Sc(), WindowsServiceSetup.Failure());
        Step("give the service its own security identifier", Sc(), WindowsServiceSetup.SidType());
        Step("set the service environment", System32("reg.exe"), WindowsServiceSetup.Environment(l));
        Step("restrict the home to the service and SYSTEM", System32("icacls.exe"), WindowsServiceSetup.HomeAcl(l));
        Step("let the service read its binary folder", System32("icacls.exe"), WindowsServiceSetup.BinaryAcl(l));
        WritePolicy(l, execLevel);
        Step("start the service", Sc(), WindowsServiceSetup.Start());
    }

    private void EnsureLinuxAccount(ServiceLayout l)
    {
        if (run(Id, ["-u", l.Account]) == 0) return;
        Step($"create the {l.Account} account", UserAdd, SystemdUnit.UserAddArgs(l));
        undo.Add(("account created", () => Step("remove the account", UserDel, [l.Account])));
    }

    private void EnsureMacAccount(ServiceLayout l)
    {
        if (run(Dscl, LaunchdDaemon.AccountExistsArgs(l)) == 0) return;
        var used = new HashSet<int>();
        foreach (var (list, key) in new[] { ("/Users", "UniqueID"), ("/Groups", "PrimaryGroupID") })
        {
            var text = (read ?? CaptureTool)(Dscl, [".", "-list", list, key])
                ?? throw new StepFailedException($"could not list the existing {list.TrimStart('/').ToLowerInvariant()}");
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(),
                        NumberStyles.None, CultureInfo.InvariantCulture, out var id)) used.Add(id);
            }
        }

        var free = Enumerable.Range(LaunchdDaemon.FirstId, LaunchdDaemon.LastId - LaunchdDaemon.FirstId + 1).Reverse().Where(i => !used.Contains(i)).ToList();
        if (free.Count == 0) throw new StepFailedException($"no free user and group id between {LaunchdDaemon.FirstId} and {LaunchdDaemon.LastId}");
        undo.Add(("account created", () =>
        {
            foreach (var args in LaunchdDaemon.AccountDeleteCommands(l)) run(Dscl, args); // a half-made account may lack either record
        }
        ));
        foreach (var args in LaunchdDaemon.AccountCommands(l, free[0])) Step($"create the {l.Account} account", Dscl, args);
    }

    /// <summary>Copies to a temp name next to the target and renames: a service never starts from a half-written binary.</summary>
    private void InstallBinary(ServiceLayout l, string currentBinary)
    {
        EnsureAdminDir(l.BinaryDir);
        if (string.Equals(Path.GetFullPath(currentBinary), Path.GetFullPath(l.Binary), StringComparison.Ordinal)) return;
        var existed = File.Exists(l.Binary);
        var temp = $"{l.Binary}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(currentBinary, temp, overwrite: false);
            Chmod(temp, Mode755);
            File.Move(temp, l.Binary, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }

        if (!existed) undo.Add(("binary copied", () => File.Delete(l.Binary)));
    }

    private void PrepareHome(ServiceLayout l)
    {
        var existed = Directory.Exists(l.Home);
        EnsureDir(l.Home, Mode700);
        if (!existed) undo.Add(("home created", () => Directory.Delete(l.Home, recursive: false)));
        if (os != OsKinds.Windows) Step("hand the home to the service account", Chown(), [$"{l.Account}:{l.Account}", l.Home]);
    }

    /// <summary>Waits until "sc query" no longer reports the service as running or stopping (at most stopWaitSeconds, one look a second).</summary>
    private void WaitStopped()
    {
        for (var i = 0; i < stopWaitSeconds; i++)
        {
            var state = (read ?? CaptureTool)(Sc(), WindowsServiceSetup.Query()) ?? "";
            if (!state.Contains("RUNNING", StringComparison.Ordinal) && !state.Contains("STOP_PENDING", StringComparison.Ordinal)) return;
            (sleep ?? Thread.Sleep)(TimeSpan.FromSeconds(1));
        }

        output.WriteLine($"The old service did not stop within {stopWaitSeconds} s; continuing.");
    }

    private void WritePolicy(ServiceLayout l, string execLevel)
    {
        EnsureAdminDir(Path.GetDirectoryName(l.ExecConfigPath)!);
        var existed = File.Exists(l.ExecConfigPath);
        // A reinstall (an upgrade) keeps the ceiling an admin wrote by hand.
        var before = ExecPolicyLoader.ReadExisting(l.ExecConfigPath);
        ExecPolicyLoader.Save(l.ExecConfigPath, new ExecPolicy(execLevel, before?.AllowedExecutables ?? [], before?.AllowedRoots ?? []));
        if (!existed) undo.Add(("exec policy written", () => File.Delete(l.ExecConfigPath)));
    }

    private void WriteDefinition(string text)
    {
        var path = Definition();
        var existed = File.Exists(path);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, text);
            Chmod(temp, Mode644);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }

        if (!existed) undo.Add(("service definition written", () => File.Delete(path)));
    }

    /// <summary>A folder only an admin may change: not a link, mode 0755 and owned by root; on Windows it inherits its ACL.</summary>
    private void EnsureAdminDir(string path)
    {
        var existed = Directory.Exists(path);
        EnsureDir(path, Mode755);
        if (!existed) undo.Add(("folder created", () => Directory.Delete(path, recursive: false)));
        if (os != OsKinds.Windows) Step("make the folder admin-owned", Chown(), [$"root:{(os == OsKinds.MacOs ? "wheel" : "root")}", path]);
    }

    private static void EnsureDir(string path, UnixFileMode mode)
    {
        if (new DirectoryInfo(path).LinkTarget is not null) throw new StepFailedException($"{path} is a symbolic link; it is not used.");
        Directory.CreateDirectory(path);
        Chmod(path, mode);
    }

    private static void Chmod(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }

    private void Step(string what, string tool, IReadOnlyList<string> args)
    {
        var code = run(tool, args);
        if (code == 0) return;
        throw new StepFailedException(code == ToolNotFound ? $"could not {what}: {tool} was not found." : $"could not {what}: {Path.GetFileName(tool)} exited with {code}.");
    }

    private void Undo()
    {
        for (var i = undo.Count - 1; i >= 0; i--)
        {
            try
            {
                undo[i].Undo();
                output.WriteLine($"Undone: {undo[i].What}.");
            }
            catch (Exception e) when (e is StepFailedException or IOException or UnauthorizedAccessException)
            {
                output.WriteLine($"Could not undo \"{undo[i].What}\": {e.Message}");
            }
        }

        undo.Clear();
    }

}
