using System.Text;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Install;

/// <summary>
/// "aipod install --standalone" and "uninstall --standalone" (ADR-0008 §3g): the interactive agent's install without a Claude Code plugin and
/// without Claude Code, plus a user-level autostart in the user's own home and account, built the way the boot service is: every external tool goes
/// through <c>run</c>, a step that fails stops the install and undoes what this run created, files are written atomically and nothing runs as root or
/// administrator. The device login runs FIRST and the autostart is enabled only once the agent is connected, so no supervisor ever restarts an agent that
/// has no identity. On macOS and Windows the autostart is an application, not a console program (decision §9.12); on Linux a <c>systemd --user</c> unit.
/// </summary>
public sealed class StandaloneInstaller(TextWriter output, Func<string, IReadOnlyList<string>, int> run, Func<string, IReadOnlyList<string>, string?> read,
    StandaloneLayout layout, bool isRoot, Func<bool> connected, Func<string?, Task<int>> login, Func<uint?> uid, string version, StandaloneLayout? legacy = null)
{
    private const string Systemctl = "/usr/bin/systemctl";
    private const string Loginctl = "/usr/bin/loginctl";
    private const string Launchctl = "/bin/launchctl";

    private readonly List<(string What, Action Undo)> undo = [];

    private sealed class StepFailedException(string message) : Exception(message);

    public async Task<int> InstallAsync(string currentBinary, string? server)
    {
        ArgumentException.ThrowIfNullOrEmpty(currentBinary);
        if (isRoot)
        {
            output.WriteLine("A standalone agent runs as you, not as root or administrator: run this from your own account (a boot service for a server is `install --service`).");
            return 2;
        }

        if (layout.Os == OsKinds.Linux && StandaloneUnits.CheckSystemd(layout) is { } problem)
        {
            output.WriteLine($"Not installed: {problem}");
            return 1;
        }

        if (layout.Os == OsKinds.Linux && run(Systemctl, ["--user", "show-environment"]) != 0)
        {
            output.WriteLine("Not installed: this account has no systemd user session; only systemd is supported on Linux.");
            return 1;
        }

        // The login comes first: the autostart is enabled only once the agent is connected.
        if (!connected())
        {
            if (string.IsNullOrWhiteSpace(server))
            {
                output.WriteLine("Not connected: give the server, aipod install --standalone --server https://<your aipod server address>, or run aipod login --server <url> first.");
                return 2;
            }

            output.WriteLine("Connecting this machine first: approve the code on the web.");
            if (await login(server) != 0 || !connected())
            {
                output.WriteLine("Not installed: the login did not complete, so nothing was enabled. Run the command again when you can approve the code.");
                return 1;
            }
        }

        var effective = layout;
        try
        {
            effective = CopyBinaries(currentBinary);
            if (effective.BundlePath is not null) WriteBundle(effective);
            WriteDefinition(effective);
            ReplaceLegacy();
            Enable(effective);
        }
        catch (Exception e) when (e is StepFailedException or IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException)
        {
            output.WriteLine($"Install failed: {e.Message}");
            Undo();
            return 1;
        }

        output.WriteLine($"The standalone agent is installed and starts when you log in. Binary: {effective.Binary}; home: {effective.Home}.");
        NextSteps(effective);
        return 0;
    }

    /// <summary>
    /// Stops the autostart and removes its definition and the application bundle, under the aipod names and, when the old layout is known, under the old ones too (an agent installed before the
    /// rename). The home, the login and the binary copy stay.
    /// </summary>
    public int Uninstall()
    {
        var any = false;
        foreach (var l in new[] { layout, legacy }.Where(l => l is not null).Cast<StandaloneLayout>())
        {
            var code = RemoveRegistration(l, report: false, out var found);
            if (code != 0) return code;
            any |= found;
        }

        if (!any)
        {
            output.WriteLine("The standalone autostart is not installed.");
            return 0;
        }

        output.WriteLine($"The standalone autostart is removed. Kept: the home {layout.Home} (the login and the queue) and the binary {layout.Binary}.");
        return 0;
    }

    /// <summary>Removes one registration; <paramref name="found"/> says whether there was one. Returns 0, or the exit code to give.</summary>
    private int RemoveRegistration(StandaloneLayout l, bool report, out bool found)
    {
        found = false;
        try
        {
            switch (l.Os)
            {
                case OsKinds.Windows:
                    if (run(Schtasks(), ["/Query", "/TN", l.Names.TaskName]) != 0 && !File.Exists(l.DefinitionPath)) return Absent(report);
                    run(Schtasks(), ["/End", "/TN", l.Names.TaskName]); // not running is fine
                    run(Schtasks(), ["/Delete", "/TN", l.Names.TaskName, "/F"]);
                    break;
                case OsKinds.MacOs:
                    if (!File.Exists(l.DefinitionPath)) return Absent(report);
                    run(Launchctl, ["bootout", $"gui/{uid() ?? 0}/{l.Names.Label}"]); // not loaded is fine
                    break;
                case OsKinds.Linux:
                    if (!File.Exists(l.DefinitionPath)) return Absent(report);
                    Step("stop and disable the unit", Systemctl, ["--user", "disable", "--now", l.Names.UnitName]);
                    break;
                default:
                    output.WriteLine($"A standalone agent is not supported on this OS ({l.Os}).");
                    return 2;
            }

            found = true;
            if (File.Exists(l.DefinitionPath)) File.Delete(l.DefinitionPath);
            if (l.BundlePath is { } bundle && Directory.Exists(bundle)) Directory.Delete(bundle, recursive: true);
            if (l.Os == OsKinds.Linux) run(Systemctl, ["--user", "daemon-reload"]);
            return 0;
        }
        catch (Exception e) when (e is StepFailedException or IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Uninstall failed: {e.Message}");
            return 1;
        }
    }

    private int Absent(bool report)
    {
        if (report) output.WriteLine("The standalone autostart is not installed.");
        return 0;
    }

    /// <summary>
    /// An install since the rename replaces a registration under the old names (ADR-0013, tier 2): its supervisor is stopped and its definition and bundle removed before the new one is enabled,
    /// so a machine is never started by both. The daemon lock keeps a second daemon out anyway.
    /// </summary>
    private void ReplaceLegacy()
    {
        if (legacy is null || legacy.Names == layout.Names) return;
        var code = RemoveRegistration(legacy, report: false, out var found);
        if (code != 0) throw new StepFailedException("could not remove the autostart under the old names");
        if (found) output.WriteLine("Replaced the autostart that had the old names (com.claudemonitor.agent.user / claude-monitor-agent / ClaudeMonitorAgent).");
    }

    /// <summary>The console binary into the home, and the application's executable (macOS) or the windowless build (Windows) beside it.</summary>
    private StandaloneLayout CopyBinaries(string currentBinary)
    {
        CopyAtomic(currentBinary, layout.Binary, "copy the binary");
        switch (layout.Os)
        {
            case OsKinds.MacOs:
                CopyAtomic(currentBinary, layout.AutostartBinary, "put the binary into the application bundle");
                return layout;
            case OsKinds.Windows:
                var windowless = Path.Combine(Path.GetDirectoryName(currentBinary) ?? "", StandaloneLayout.WindowlessName);
                if (File.Exists(windowless))
                {
                    CopyAtomic(windowless, layout.AutostartBinary, "copy the windowless build");
                    return layout;
                }

                // The release carries one binary: the windowless one is the same file with its subsystem set to GUI.
                CopyAtomic(currentBinary, layout.AutostartBinary, "copy the program for the autostart");
                if (PeSubsystem.TrySetGui(layout.AutostartBinary)) return layout;
                DeleteFile(layout.AutostartBinary);
                output.WriteLine($"Note: a windowless copy could not be made, so the autostart uses the console build and Windows may show a console window at logon.");
                return layout with { AutostartBinary = layout.Binary };
            default:
                return layout;
        }
    }

    private void WriteBundle(StandaloneLayout l)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(l.InfoPlistPath)!);
        undo.Add(("the application bundle", () => DeleteTree(l.BundlePath!)));
        WriteAtomic(l.InfoPlistPath, StandaloneUnits.RenderInfoPlist(version, l.Names.BundleId), "write the bundle's Info.plist");
    }

    private void WriteDefinition(StandaloneLayout l)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(l.DefinitionPath)!);
        undo.Add(("the autostart definition", () => DeleteFile(l.DefinitionPath)));
        switch (l.Os)
        {
            case OsKinds.MacOs:
                WriteAtomic(l.DefinitionPath, StandaloneUnits.RenderLaunchAgent(l), "write the LaunchAgent");
                break;
            case OsKinds.Linux:
                WriteAtomic(l.DefinitionPath, StandaloneUnits.RenderSystemd(l), "write the systemd user unit");
                break;
            default:
                WriteAtomic(l.DefinitionPath, StandaloneUnits.RenderLogonTask(l), "write the logon task", Encoding.Unicode);
                break;
        }
    }

    private void Enable(StandaloneLayout l)
    {
        switch (l.Os)
        {
            case OsKinds.MacOs:
                var domain = $"gui/{uid() ?? 0}";
                run(Launchctl, ["bootout", $"{domain}/{l.Names.Label}"]); // a reinstall replaces what is loaded
                Step("load the LaunchAgent", Launchctl, ["bootstrap", domain, l.DefinitionPath]);
                undo.Add(("the loaded LaunchAgent", () => run(Launchctl, ["bootout", $"{domain}/{l.Names.Label}"])));
                Step("start the agent", Launchctl, ["kickstart", "-k", $"{domain}/{l.Names.Label}"]);
                break;
            case OsKinds.Linux:
                Step("reload systemd", Systemctl, ["--user", "daemon-reload"]);
                Step("enable and start the unit", Systemctl, ["--user", "enable", "--now", l.Names.UnitName]);
                undo.Add(("the enabled unit", () => run(Systemctl, ["--user", "disable", "--now", l.Names.UnitName])));
                break;
            default:
                Step("create the logon task", Schtasks(), ["/Create", "/TN", l.Names.TaskName, "/XML", l.DefinitionPath, "/F"]);
                undo.Add(("the logon task", () => run(Schtasks(), ["/Delete", "/TN", l.Names.TaskName, "/F"])));
                Step("start the agent now", Schtasks(), ["/Run", "/TN", l.Names.TaskName]);
                break;
        }
    }

    private void NextSteps(StandaloneLayout l)
    {
        if (l.Os == OsKinds.Linux && read(Loginctl, ["show-user", l.User, "-p", "Linger"])?.Trim() == "Linger=no")
        {
            output.WriteLine($"Note: without lingering the agent runs only while you are logged in. To keep it running after you log out: sudo loginctl enable-linger {l.User}");
        }

        output.WriteLine("Run `aipod status` to check it. This machine reports no Claude Code (claude=none); other machines can still reach it as a target.");
    }

    private void Step(string what, string tool, IReadOnlyList<string> args)
    {
        var code = run(tool, args);
        if (code == 0) return;
        throw new StepFailedException(code == ServiceInstaller.ToolNotFound ? $"could not {what}: {tool} was not found" : $"could not {what} ({Path.GetFileName(tool)} exited {code})");
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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                output.WriteLine($"Could not undo {undo[i].What}: {e.GetType().Name}.");
            }
        }

        undo.Clear();
    }

    private void CopyAtomic(string from, string to, string what)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.Ordinal)) return;
        var existed = File.Exists(to);
        var temp = to + ".tmp";
        try
        {
            File.Copy(from, temp, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(temp, to, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteFile(temp);
            throw new StepFailedException($"could not {what}: {e.GetType().Name}");
        }

        if (!existed) undo.Add((what, () => DeleteFile(to)));
    }

    private static void WriteAtomic(string path, string text, string what, Encoding? encoding = null)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, text, encoding ?? new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteFile(temp);
            throw new StepFailedException($"could not {what}: {e.GetType().Name}");
        }
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void DeleteTree(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static string Schtasks() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
}
