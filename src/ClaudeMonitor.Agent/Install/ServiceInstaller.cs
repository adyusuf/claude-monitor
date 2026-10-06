using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Install;

/// <summary>
/// "cm-agent install --service" and "uninstall --service" (ADR-0004): registers the daemon as a boot service under a
/// dedicated account, from an admin-owned copy of the binary. Stops at the first failure, says what failed and undoes
/// what this run created. Every external tool goes through <c>run</c>, which returns its exit code.
/// </summary>
public sealed partial class ServiceInstaller(TextWriter output, Func<string, IReadOnlyList<string>, int> run, string os, bool isAdmin,
    ServiceLayout? layout = null, string? definitionPath = null, string? systemdRunDir = null,
    Func<string, IReadOnlyList<string>, string?>? read = null)
{
    public const int ToolNotFound = 127;

    private const string Systemctl = "/usr/bin/systemctl";
    private const string UserAdd = "/usr/sbin/useradd";
    private const string UserDel = "/usr/sbin/userdel";
    private const string Id = "/usr/bin/id";
    private const string LinuxChown = "/bin/chown";
    private const string MacChown = "/usr/sbin/chown";
    private const string Launchctl = "/bin/launchctl";
    private const string Dscl = "/usr/bin/dscl";


    private static readonly string[] PrivilegedAccounts = ["root", "system", "localsystem", @"nt authority\system"];

    private readonly List<(string What, Action Undo)> undo = [];
    private ServiceLayout? resolved;

    private sealed class StepFailedException(string message) : Exception(message);

    public int Install(string currentBinary, string execLevel, bool allowRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(currentBinary);
        if (!ExecLevels.All.Contains(execLevel))
        {
            output.WriteLine($"--exec takes off, argv or shell, not \"{execLevel}\".");
            return 2;
        }

        if (!Supported()) return Unsupported();
        var l = Layout();
        if (!isAdmin) return NeedAdmin(currentBinary, execLevel, allowRoot);
        if (PrivilegedAccounts.Contains(l.Account.ToLowerInvariant()))
        {
            if (!allowRoot)
            {
                output.WriteLine($"Refusing to run the service as {l.Account}. --allow-root does it for metrics and uploads only; remote execution stays off.");
                return 2;
            }

            execLevel = ExecLevels.Off;
            output.WriteLine("--allow-root: the service runs privileged, so remote execution stays off.");
        }

        var problem = Problem(l) ?? Precondition();
        if (problem is not null)
        {
            output.WriteLine($"Not installed: {problem}");
            return 1;
        }

        try
        {
            switch (os)
            {
                case OsKinds.Windows:
                    InstallWindows(l, currentBinary, execLevel);
                    break;
                case OsKinds.MacOs:
                case OsKinds.Linux:
                    InstallUnix(l, currentBinary, execLevel);
                    break;
                default:
                    return Unsupported();
            }
        }
        catch (Exception e) when (e is StepFailedException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            output.WriteLine($"Install failed: {e.Message}");
            Undo();
            return 1;
        }

        output.WriteLine($"The service is installed and started. Binary: {l.Binary}; home: {l.Home}; exec policy: {l.ExecConfigPath} ({execLevel}).");
        NextSteps(l);
        return 0;
    }

    /// <summary>Stops and removes the service. The home and the exec policy stay: they hold the login and the admin's decision.</summary>
    public int Uninstall()
    {
        if (!Supported()) return Unsupported();
        var l = Layout();
        if (!isAdmin) return NeedAdmin(null, null, false);
        try
        {
            switch (os)
            {
                case OsKinds.Windows:
                    if (run(Sc(), WindowsServiceSetup.Query()) != 0)
                    {
                        output.WriteLine("The service is not installed.");
                        return 0;
                    }

                    run(Sc(), WindowsServiceSetup.Stop()); // already stopped is fine
                    Step("remove the service", Sc(), WindowsServiceSetup.Delete());
                    break;
                case OsKinds.MacOs:
                case OsKinds.Linux:
                    if (!File.Exists(Definition()))
                    {
                        output.WriteLine("The service is not installed.");
                        return 0;
                    }

                    if (os == OsKinds.Linux) Step("stop and disable the service", Systemctl, ["disable", "--now", ServiceLayout.ServiceName]);
                    else run(Launchctl, ["bootout", $"system/{LaunchdDaemon.Label}"]); // not loaded is fine
                    File.Delete(Definition());
                    if (os == OsKinds.Linux) Step("reload systemd", Systemctl, ["daemon-reload"]);
                    break;
                default:
                    return Unsupported();
            }
        }
        catch (Exception e) when (e is StepFailedException or IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Uninstall failed: {e.Message}");
            return 1;
        }

        output.WriteLine("The service is removed. Kept: the home " + l.Home + ", the exec policy " + l.ExecConfigPath + " and the binary " + l.Binary + ".");
        return 0;
    }

    private string? Problem(ServiceLayout l) => os switch
    {
        OsKinds.Linux => SystemdUnit.Check(l),
        OsKinds.Windows => WindowsServiceSetup.Check(l),
        _ => null,
    };

    private string? Precondition() =>
        os == OsKinds.Linux && !Directory.Exists(systemdRunDir ?? SystemdUnit.RunDir) ? "this machine does not run systemd; only systemd is supported on Linux." : null;

    private void NextSteps(ServiceLayout l)
    {
        var log = os switch
        {
            OsKinds.Linux => $"sudo journalctl -u {ServiceLayout.ServiceName}   or   sudo cat {Quote(l.LoginCodePath)}",
            OsKinds.MacOs => $"sudo cat {Quote(l.LoginCodePath)}",
            _ => $"type \"{l.LoginCodePath}\"   (in an elevated prompt)",
        };
        output.WriteLine(l.Server is null
            ? "Not connected: install again with --server https://<your Claude Monitor address> so the service can log in by itself."
            : "The service logs in by itself: it shows a code to approve on the web. Read it with:");
        if (l.Server is not null) output.WriteLine($"  {log}");
        output.WriteLine("Then approve the code on the web while signed in; the service starts reporting at once.");
    }

    private int NeedAdmin(string? binary, string? execLevel, bool allowRoot)
    {
        var command = binary is null
            ? $"{Quote(Environment.ProcessPath ?? "cm-agent")} uninstall --service"
            : $"{Quote(binary)} install --service --exec {execLevel}{(allowRoot ? " --allow-root" : "")}";
        output.WriteLine(os == OsKinds.Windows
            ? $"The service needs an administrator. Run this from an elevated prompt (Run as administrator):  {command}"
            : $"The service needs root. Run:  sudo {command}");
        return 2;
    }

    private int Unsupported()
    {
        output.WriteLine($"A service is not supported on this OS ({os}).");
        return 2;
    }

    private bool Supported() => os is OsKinds.Linux or OsKinds.MacOs or OsKinds.Windows;
    private ServiceLayout Layout() => resolved ??= layout ?? ServiceLayout.For(os);
    private string Definition() => definitionPath ?? (os == OsKinds.Linux ? SystemdUnit.UnitPath : LaunchdDaemon.PlistPath);
    private string Chown() => os == OsKinds.MacOs ? MacChown : LinuxChown;
    private static string Sc() => System32("sc.exe");
    private static string System32(string tool) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), tool);
    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
