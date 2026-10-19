using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Install;

/// <summary>The steps of <see cref="ServiceMigrator"/>: starting, stopping and disabling a service on each OS, copying a home and a policy, and removing the old service for good.</summary>
public sealed partial class ServiceMigrator
{
    private const string LinuxChown = "/bin/chown";
    private const string MacChown = "/usr/sbin/chown";

    /// <summary>The exec policy files an admin owns beside each other; nothing else in that folder is ours to copy or delete (on macOS and Windows it also holds the binary and the home).</summary>
    private static readonly string[] PolicyFiles = ["exec.json", "approvers.json"];

    /// <summary>What is not copied into the new home: the extraction folder of the bundle, the lock and pid files of a daemon that is not running, the login code, the scratch folder of runs.</summary>
    private static readonly string[] SkippedNames = [".net", "daemon.lock", "daemon.pid", "update.lock", "claude-update.lock", ServiceLayout.LoginCodeFile, MarkerFile, RunAccount.WorkFolderName];

    private bool Skip(string name) =>
        SkippedNames.Contains(name, StringComparer.Ordinal)
        || (os == OsKinds.Windows && name.StartsWith("cred-", StringComparison.Ordinal) && name.EndsWith(".bin", StringComparison.Ordinal)); // DPAPI of the old account: the new one cannot read it

    private string Chown() => os == OsKinds.MacOs ? MacChown : LinuxChown;

    private void Step(string what, string tool, IReadOnlyList<string> args)
    {
        var code = run(tool, args);
        if (code == 0) return;
        throw new StepFailedException(code == ServiceInstaller.ToolNotFound ? $"could not {what}: {tool} was not found." : $"could not {what}: {Path.GetFileName(tool)} exited with {code}.");
    }

    // ---- the service itself --------------------------------------------------------------------------------------------

    private void StopService(ServiceLayout l, string what)
    {
        switch (os)
        {
            case OsKinds.Linux:
                Step($"stop {what}", Systemctl, ["stop", l.Names.ServiceName]);
                break;
            case OsKinds.MacOs:
                run(Launchctl, ["bootout", $"system/{l.Names.Label}"]); // not loaded is fine
                break;
            default:
                run(ServiceInstaller.Sc(), WindowsServiceSetup.Stop(l.Names.ServiceName));
                WaitStopped(l);
                break;
        }
    }

    private void StartService(ServiceLayout l)
    {
        switch (os)
        {
            case OsKinds.Linux:
                Step("start the service", Systemctl, ["start", l.Names.ServiceName]);
                break;
            case OsKinds.MacOs:
                Step("load the service", Launchctl, ["bootstrap", "system", l.Names.PlistPath]);
                break;
            default:
                Step("start the service", ServiceInstaller.Sc(), WindowsServiceSetup.Start(l.Names.ServiceName));
                break;
        }
    }

    /// <summary>Kept, not removed: the definition and the files stay, only the start at boot (and now) is off.</summary>
    private void DisableService(ServiceLayout l, string what)
    {
        switch (os)
        {
            case OsKinds.Linux:
                Step($"disable {what}", Systemctl, ["disable", l.Names.ServiceName]);
                break;
            case OsKinds.MacOs:
                run(Launchctl, ["bootout", $"system/{l.Names.Label}"]); // may already be unloaded
                Step($"disable {what}", Launchctl, ["disable", $"system/{l.Names.Label}"]);
                break;
            default:
                run(ServiceInstaller.Sc(), WindowsServiceSetup.Stop(l.Names.ServiceName)); // may already be stopped
                WaitStopped(l);
                Step($"disable {what}", ServiceInstaller.Sc(), WindowsServiceSetup.StartType(l.Names.ServiceName, disabled: true));
                break;
        }
    }

    private void EnableService(ServiceLayout l, string what)
    {
        switch (os)
        {
            case OsKinds.Linux:
                Step($"enable and start {what}", Systemctl, ["enable", "--now", l.Names.ServiceName]);
                break;
            case OsKinds.MacOs:
                Step($"enable {what}", Launchctl, ["enable", $"system/{l.Names.Label}"]);
                Step($"load {what}", Launchctl, ["bootstrap", "system", l.Names.PlistPath]);
                break;
            default:
                Step($"enable {what}", ServiceInstaller.Sc(), WindowsServiceSetup.StartType(l.Names.ServiceName, disabled: false));
                Step($"start {what}", ServiceInstaller.Sc(), WindowsServiceSetup.Start(l.Names.ServiceName));
                break;
        }
    }

    /// <summary>"sc stop" returns at once and a running binary stays locked: look once a second, at most thirty times.</summary>
    private void WaitStopped(ServiceLayout l)
    {
        for (var i = 0; i < 30; i++)
        {
            var state = (read ?? ServiceInstaller.CaptureTool)(ServiceInstaller.Sc(), WindowsServiceSetup.Query(l.Names.ServiceName)) ?? "";
            if (!state.Contains("RUNNING", StringComparison.Ordinal) && !state.Contains("STOP_PENDING", StringComparison.Ordinal)) return;
            (sleep ?? Thread.Sleep)(TimeSpan.FromSeconds(1));
        }

        output.WriteLine(Strings.T(Msg.MigDidNotStop, l.Names.ServiceName));
    }

    /// <summary>Undo of the new installation: the service and its definition, the copied binary and (not the run account, which the old service shares) the account.</summary>
    private void RemoveNewService(ServiceLayout target)
    {
        new ServiceInstaller(output, run, os, isAdmin, target, systemdRunDir: systemdRunDir, read: read, sleep: sleep) { SudoersFolder = sudoersFolder ?? "/etc/sudoers.d" }.Uninstall();
        if (File.Exists(target.Binary)) File.Delete(target.Binary);
        if (Directory.Exists(target.BinaryDir) && !Directory.EnumerateFileSystemEntries(target.BinaryDir).Any()) Directory.Delete(target.BinaryDir);
        switch (os)
        {
            case OsKinds.Linux:
                run(UserDel, [target.Account]);
                break;
            case OsKinds.MacOs:
                foreach (var args in LaunchdDaemon.AccountDeleteCommands(target)) run(Dscl, args);
                break;
            default:
                // The Windows home was made by the installer and filled afterwards: nothing of it is the old service's.
                if (Directory.Exists(target.Home)) Directory.Delete(target.Home, recursive: true);
                break;
        }
    }

    // ---- files ---------------------------------------------------------------------------------------------------------

    private void CopyHome()
    {
        CopyTree(from.Home, to.Home);
        undo.Add(("home copied", () =>
        {
            if (Directory.Exists(to.Home)) Directory.Delete(to.Home, recursive: true);
        }
        ));
    }

    private void CopyPolicy()
    {
        var fromDir = Path.GetDirectoryName(from.ExecConfigPath)!;
        var toDir = Path.GetDirectoryName(to.ExecConfigPath)!;
        var names = PolicyFiles.Where(n => File.Exists(Path.Combine(fromDir, n)) && new FileInfo(Path.Combine(fromDir, n)).LinkTarget is null).ToList();
        if (names.Count == 0) return; // no policy was ever written: the new install writes a fresh one (off)
        var dirExisted = Directory.Exists(toDir);
        Directory.CreateDirectory(toDir);
        foreach (var name in names) File.Copy(Path.Combine(fromDir, name), Path.Combine(toDir, name), overwrite: true);
        undo.Add(("policy copied", () =>
        {
            foreach (var name in names) File.Delete(Path.Combine(toDir, name));
            if (!dirExisted && Directory.Exists(toDir) && !Directory.EnumerateFileSystemEntries(toDir).Any()) Directory.Delete(toDir);
        }
        ));
    }

    /// <summary>
    /// Windows: the home is filled after the service exists, so the files take the ACL the installer set on the home. The identity keeps its machine key and server but loses its agent and
    /// workspace ids: the tokens could not be carried over (DPAPI), so the service logs in again, and an administrator approves a code.
    /// </summary>
    private void CopyHomeOntoRunningWindowsService(ServiceLayout target)
    {
        StopService(target, "the new service");
        CopyTree(from.Home, target.Home);
        var config = AgentConfig.FromEnvironment(k => k == "CM_AGENT_HOME" ? target.Home : null);
        if (Identity.Peek(config) is { } identity) (identity with { AgentId = null, WorkspaceId = null }).Save(config);
        Step("start the new service", ServiceInstaller.Sc(), WindowsServiceSetup.Start(target.Names.ServiceName));
    }

    /// <summary>Copies a tree without following links (a link is reported and left), keeping the modes on Unix.</summary>
    private void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
            {
                output.WriteLine(Strings.T(Msg.MigLeftLink, entry.FullName));
                continue;
            }

            if (Skip(entry.Name)) continue;
            var destination = Path.Combine(target, entry.Name);
            if (entry is DirectoryInfo)
            {
                CopyTree(entry.FullName, destination);
                continue;
            }

            File.Copy(entry.FullName, destination, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(entry.FullName));
        }

        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(source));
    }

    /// <summary>Going back (Unix): the identity and the local database the new service kept up are copied to the old home, with the old account as owner, so nothing it learned is lost.</summary>
    private void BringDataBack()
    {
        // A write-ahead log left in the old home belongs to the old database and must not meet the new one: the old database files go first.
        foreach (var stale in Directory.EnumerateFiles(from.Home, "agent.db*")) File.Delete(stale);
        foreach (var path in Directory.EnumerateFiles(to.Home).Where(p => Path.GetFileName(p) is "agent.json" || Path.GetFileName(p).StartsWith("agent.db", StringComparison.Ordinal)))
        {
            var destination = Path.Combine(from.Home, Path.GetFileName(path));
            File.Copy(path, destination, overwrite: true);
            Step("hand the file back to the old account", Chown(), [$"{from.Account}:{from.Account}", destination]);
        }
    }

    /// <summary>Going back: the policy goes to the old place and out of the new one, so an agent that reads the new place first finds nothing there.</summary>
    private void BringPolicyBack()
    {
        var fromDir = Path.GetDirectoryName(from.ExecConfigPath)!;
        var toDir = Path.GetDirectoryName(to.ExecConfigPath)!;
        foreach (var name in PolicyFiles)
        {
            var path = Path.Combine(toDir, name);
            if (!File.Exists(path)) continue;
            Directory.CreateDirectory(fromDir);
            File.Copy(path, Path.Combine(fromDir, name), overwrite: true);
            File.Delete(path);
        }
    }

    // ---- the end -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>--finish</c>: the old service, its account, folders, policy and sudoers drop-in. Only the paths of the old layout are ever deleted, and never one the new layout also uses.
    /// </summary>
    private void RemoveOldFiles()
    {
        // Everything that will be deleted is checked before anything is: a layout that overlaps the new one must not leave a half-removed old service behind.
        var fromDir = Path.GetDirectoryName(from.ExecConfigPath)!;
        foreach (var path in new[] { from.Home, from.BinaryDir }.Concat(PolicyFiles.Select(n => Path.Combine(fromDir, n))))
        {
            if (Overlaps(path)) throw new StepFailedException($"{path} is also used by the aipod service; nothing was removed.");
        }

        switch (os)
        {
            case OsKinds.Linux:
                run(Systemctl, ["disable", "--now", from.Names.ServiceName]); // already off; fine if the unit is gone
                if (File.Exists(from.Names.UnitPath)) File.Delete(from.Names.UnitPath);
                Step("reload systemd", Systemctl, ["daemon-reload"]);
                run(UserDel, [from.Account]);
                break;
            case OsKinds.MacOs:
                run(Launchctl, ["bootout", $"system/{from.Names.Label}"]);
                if (File.Exists(from.Names.PlistPath)) File.Delete(from.Names.PlistPath);
                foreach (var args in LaunchdDaemon.AccountDeleteCommands(from)) run(Dscl, args);
                break;
            default:
                run(ServiceInstaller.Sc(), WindowsServiceSetup.Stop(from.Names.ServiceName));
                WaitStopped(from);
                Step("remove the old service", ServiceInstaller.Sc(), WindowsServiceSetup.Delete(from.Names.ServiceName));
                break;
        }

        var sudoers = Path.Combine(sudoersFolder ?? "/etc/sudoers.d", from.Names.SudoersFileName);
        if (os != OsKinds.Windows && File.Exists(sudoers)) File.Delete(sudoers);
        foreach (var name in PolicyFiles) DeleteFile(Path.Combine(fromDir, name));
        DeleteFolder(from.Home);
        DeleteFolder(from.BinaryDir);
        if (Directory.Exists(fromDir) && !Directory.EnumerateFileSystemEntries(fromDir).Any() && !Overlaps(fromDir)) Directory.Delete(fromDir);
        var root = Path.GetDirectoryName(from.BinaryDir)!;
        if (os == OsKinds.MacOs && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any() && !Overlaps(root)) Directory.Delete(root);
    }

    private void DeleteFile(string path)
    {
        if (Overlaps(path)) throw new StepFailedException($"{path} is also used by the aipod service; it is not deleted.");
        if (File.Exists(path)) File.Delete(path);
    }

    private void DeleteFolder(string path)
    {
        if (Overlaps(path) || Path.GetPathRoot(path) == path || path.Length < 8) throw new StepFailedException($"{path} is not a folder this migration may delete.");
        if (new DirectoryInfo(path).LinkTarget is not null) throw new StepFailedException($"{path} is a link; it is not deleted.");
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    /// <summary>True when the path is, holds or lies inside a place of the aipod layout.</summary>
    private bool Overlaps(string path)
    {
        var comparison = os == OsKinds.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var separator = Path.DirectorySeparatorChar.ToString();
        return new[] { to.Home, to.BinaryDir, to.ExecConfigPath }.Any(p =>
            p.Equals(path, comparison) || p.StartsWith(path.TrimEnd(Path.DirectorySeparatorChar) + separator, comparison) || path.StartsWith(p.TrimEnd(Path.DirectorySeparatorChar) + separator, comparison));
    }

}
