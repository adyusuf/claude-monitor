using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Install;

/// <summary>
/// "aipod install --service" and "uninstall --service" (ADR-0005): registers the daemon as a boot service under a
/// dedicated account, from an admin-owned copy of the binary. Stops at the first failure, says what failed and undoes
/// what this run created. Every external tool goes through <c>run</c>, which returns its exit code.
/// </summary>
public sealed partial class ServiceInstaller(TextWriter output, Func<string, IReadOnlyList<string>, int> run, string os, bool isAdmin,
    ServiceLayout? layout = null, string? definitionPath = null, string? systemdRunDir = null,
    Func<string, IReadOnlyList<string>, string?>? read = null, int stopWaitSeconds = ServiceInstaller.DefaultStopWaitSeconds,
    Action<TimeSpan>? sleep = null)
{
    public const int ToolNotFound = 127;
    public const int DefaultStopWaitSeconds = 30;

    private const string Systemctl = "/usr/bin/systemctl";
    private const string UserAdd = "/usr/sbin/useradd";
    private const string UserDel = "/usr/sbin/userdel";
    private const string Id = "/usr/bin/id";
    private const string LinuxChown = "/bin/chown";
    private const string MacChown = "/usr/sbin/chown";
    private const string Launchctl = "/bin/launchctl";
    private const string Dscl = "/usr/bin/dscl";


    /// <summary>A migration (ADR-0013, tier 2) copies files into the home before the account exists: they are handed to it one by one, not only the folder.</summary>
    public bool ChownHomeRecursively { get; init; }

    private static readonly string[] PrivilegedAccounts = ["root", "system", "localsystem", @"nt authority\system"];

    private readonly List<(string What, Action Undo)> undo = [];
    private ServiceLayout? resolved;

    private sealed class StepFailedException(string message) : Exception(message);

    public int Install(string currentBinary, string execLevel, bool allowRoot, string? runAccount = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(currentBinary);
        if (!ExecLevels.All.Contains(execLevel))
        {
            output.WriteLine(Strings.T(Msg.SvcExecBad, execLevel));
            return 2;
        }

        if (!Supported()) return Unsupported();
        if (runAccount is not null && os is not (OsKinds.Linux or OsKinds.MacOs))
        {
            output.WriteLine(Strings.T(Msg.SvcRunAccountWindows));
            return 2;
        }

        var l = Layout();
        if (!isAdmin) return NeedAdmin(currentBinary, execLevel, allowRoot);
        if (PrivilegedAccounts.Contains(l.Account.ToLowerInvariant()))
        {
            if (!allowRoot)
            {
                output.WriteLine(Strings.T(Msg.SvcRefuseAccount, l.Account));
                return 2;
            }

            execLevel = ExecLevels.Off;
            output.WriteLine(Strings.T(Msg.SvcAllowRoot));
        }

        var problem = Problem(l) ?? Precondition();
        if (problem is not null)
        {
            output.WriteLine(Strings.T(Msg.NotInstalled, problem));
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
                    InstallUnix(l, currentBinary, execLevel, runAccount);
                    break;
                default:
                    return Unsupported();
            }
        }
        catch (Exception e) when (e is StepFailedException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            output.WriteLine(Strings.T(Msg.InstallFailed, e.Message));
            Undo();
            return 1;
        }

        output.WriteLine(Strings.T(Msg.SvcInstalled, l.Binary, l.Home, l.ExecConfigPath, execLevel));
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
                    if (run(Sc(), WindowsServiceSetup.Query(l.Names.ServiceName)) != 0)
                    {
                        output.WriteLine(Strings.T(Msg.SvcNotInstalled));
                        return 0;
                    }

                    run(Sc(), WindowsServiceSetup.Stop(l.Names.ServiceName)); // already stopped is fine
                    Step("remove the service", Sc(), WindowsServiceSetup.Delete(l.Names.ServiceName));
                    break;
                case OsKinds.MacOs:
                case OsKinds.Linux:
                    if (!File.Exists(Definition()))
                    {
                        output.WriteLine(Strings.T(Msg.SvcNotInstalled));
                        return 0;
                    }

                    if (os == OsKinds.Linux) Step("stop and disable the service", Systemctl, ["disable", "--now", l.Names.ServiceName]);
                    else run(Launchctl, ["bootout", $"system/{l.Names.Label}"]); // not loaded is fine
                    File.Delete(Definition());
                    RemoveRunAccountAccess();
                    if (os == OsKinds.Linux) Step("reload systemd", Systemctl, ["daemon-reload"]);
                    break;
                default:
                    return Unsupported();
            }
        }
        catch (Exception e) when (e is StepFailedException or IOException or UnauthorizedAccessException)
        {
            output.WriteLine(Strings.T(Msg.UninstallFailed, e.Message));
            return 1;
        }

        output.WriteLine(Strings.T(Msg.SvcRemoved, l.Home, l.ExecConfigPath, l.Binary));
        return 0;
    }

    /// <summary>True when a service with this layout's names is registered: its unit or plist exists, or on Windows <c>sc query</c> finds it.</summary>
    public static bool IsInstalled(string os, ServiceLayout l, Func<string, IReadOnlyList<string>, int> run, string? definitionPath = null)
    {
        ArgumentNullException.ThrowIfNull(l);
        ArgumentNullException.ThrowIfNull(run);
        return os switch
        {
            OsKinds.Windows => run(Sc(), WindowsServiceSetup.Query(l.Names.ServiceName)) == 0,
            OsKinds.Linux => File.Exists(definitionPath ?? l.Names.UnitPath),
            OsKinds.MacOs => File.Exists(definitionPath ?? l.Names.PlistPath),
            _ => false,
        };
    }

    private string? Problem(ServiceLayout l) => os switch
    {
        OsKinds.Linux => SystemdUnit.Check(l),
        OsKinds.Windows => WindowsServiceSetup.Check(l),
        _ => null,
    };

    private string? Precondition() =>
        os == OsKinds.Linux && !Directory.Exists(systemdRunDir ?? SystemdUnit.RunDir) ? Strings.T(Msg.SvcNoSystemd) : null;

    private void NextSteps(ServiceLayout l)
    {
        var log = os switch
        {
            OsKinds.Linux => $"sudo journalctl -u {l.Names.ServiceName}   or   sudo cat {Quote(l.LoginCodePath)}",
            OsKinds.MacOs => $"sudo cat {Quote(l.LoginCodePath)}",
            _ => $"type \"{l.LoginCodePath}\"   (in an elevated prompt)",
        };
        output.WriteLine(Strings.T(l.Server is null ? Msg.SvcNotConnected : Msg.SvcLogsInItself));
        if (l.Server is not null) output.WriteLine($"  {log}");
        output.WriteLine(Strings.T(Msg.SvcApproveCode));
    }

    private int NeedAdmin(string? binary, string? execLevel, bool allowRoot)
    {
        var command = binary is null
            ? $"{Quote(Environment.ProcessPath ?? "cm-agent")} uninstall --service"
            : $"{Quote(binary)} install --service --exec {execLevel}{(allowRoot ? " --allow-root" : "")}";
        output.WriteLine(Strings.T(os == OsKinds.Windows ? Msg.SvcNeedAdminWindows : Msg.SvcNeedRoot, command));
        return 2;
    }

    private int Unsupported()
    {
        output.WriteLine(Strings.T(Msg.SvcUnsupportedOs, os));
        return 2;
    }

    private bool Supported() => os is OsKinds.Linux or OsKinds.MacOs or OsKinds.Windows;
    private ServiceLayout Layout() => resolved ??= layout ?? ServiceLayout.For(os);
    private string Definition() => definitionPath ?? (os == OsKinds.Linux ? Layout().Names.UnitPath : Layout().Names.PlistPath);
    private string Chown() => os == OsKinds.MacOs ? MacChown : LinuxChown;
    internal static string Sc() => System32("sc.exe");
    private static string System32(string tool) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), tool);
    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
