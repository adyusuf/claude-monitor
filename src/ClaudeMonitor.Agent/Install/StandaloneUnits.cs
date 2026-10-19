using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace ClaudeMonitor.Agent.Install;

/// <summary>
/// The user-level autostart definitions of a standalone agent (ADR-0008 §3g), built the way the boot service's are (ADR-0005): rendered from the layout
/// alone, a path that would need quoting is refused rather than escaped, and nothing starts as root. The daemon they start does not exit when it is not
/// connected (it waits and re-reads its identity), so none of these supervisors restarts it in a loop.
/// </summary>
public static partial class StandaloneUnits
{
    // at least two segments, no space, %, $, quote, backslash, semicolon or newline: the same rule as the system unit
    [GeneratedRegex(@"^(/[A-Za-z0-9._+@-]+){2,}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SafeUnixPath();

    /// <summary>Null when the layout can be written into a systemd unit; otherwise why not.</summary>
    public static string? CheckSystemd(StandaloneLayout l)
    {
        ArgumentNullException.ThrowIfNull(l);
        foreach (var (what, path) in new[] { ("binary", l.AutostartBinary), ("home", l.Home) })
        {
            if (!SafeUnixPath().IsMatch(path) || path.Split('/').Contains("..")) return $"the {what} path \"{path}\" needs quoting or is not absolute; it is refused";
        }

        return null;
    }

    /// <summary>The <c>systemd --user</c> unit: <c>Restart=on-failure</c>, ten seconds apart, with a widened start limit.</summary>
    public static string RenderSystemd(StandaloneLayout l)
    {
        if (CheckSystemd(l) is { } problem) throw new ArgumentException(problem, nameof(l));
        List<string> lines =
        [
            "[Unit]",
            "Description=aipod agent (standalone, this user)",
            "After=network-online.target",
            "Wants=network-online.target",
            "StartLimitIntervalSec=600",
            "StartLimitBurst=20",
            "",
            "[Service]",
            "Type=simple",
            $"ExecStart={l.AutostartBinary} daemon",
        ];
        lines.AddRange(l.Environment.Select(e => $"Environment={e.Name}={e.Value}"));
        lines.AddRange(["Restart=on-failure", "RestartSec=10", "KillMode=control-group", "UMask=0077", "Nice=10", "", "[Install]", "WantedBy=default.target"]);
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>
    /// The LaunchAgent: it starts the executable inside the application bundle (an app, so no terminal and no window), at login, and keeps it running
    /// only after an unsuccessful exit: a second instance that finds the daemon lock taken exits 0 and is not restarted.
    /// </summary>
    public static string RenderLaunchAgent(StandaloneLayout l)
    {
        ArgumentNullException.ThrowIfNull(l);
        if (!l.AutostartBinary.StartsWith('/') || !l.Home.StartsWith('/')) throw new ArgumentException("the binary and home must be absolute paths", nameof(l));
        var b = new StringBuilder();
        b.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        b.AppendLine("""<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">""");
        b.AppendLine("""<plist version="1.0">""");
        b.AppendLine("<dict>");
        b.AppendLine($"  <key>Label</key><string>{Esc(StandaloneLayout.Label)}</string>");
        b.AppendLine("  <key>ProgramArguments</key>");
        b.AppendLine($"  <array><string>{Esc(l.AutostartBinary)}</string><string>daemon</string></array>");
        b.AppendLine("  <key>EnvironmentVariables</key>");
        b.AppendLine("  <dict>");
        foreach (var (name, value) in l.Environment) b.AppendLine($"    <key>{Esc(name)}</key><string>{Esc(value)}</string>");
        b.AppendLine("  </dict>");
        b.AppendLine("  <key>RunAtLoad</key><true/>");
        b.AppendLine("  <key>KeepAlive</key>");
        b.AppendLine("  <dict><key>SuccessfulExit</key><false/></dict>");
        b.AppendLine("  <key>ProcessType</key><string>Background</string>");
        b.AppendLine("  <key>LowPriorityIO</key><true/>");
        b.AppendLine("</dict>");
        b.AppendLine("</plist>");
        return b.ToString();
    }

    /// <summary>The bundle's Info.plist: an agent application with no Dock icon and no menu (LSUIElement).</summary>
    public static string RenderInfoPlist(string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);
        var b = new StringBuilder();
        b.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        b.AppendLine("""<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">""");
        b.AppendLine("""<plist version="1.0">""");
        b.AppendLine("<dict>");
        b.AppendLine("  <key>CFBundleIdentifier</key><string>com.claudemonitor.agent.app</string>");
        b.AppendLine("  <key>CFBundleName</key><string>aipod</string>");
        b.AppendLine("  <key>CFBundleDisplayName</key><string>aipod</string>");
        b.AppendLine("  <key>CFBundleExecutable</key><string>cm-agent</string>");
        b.AppendLine("  <key>CFBundlePackageType</key><string>APPL</string>");
        b.AppendLine($"  <key>CFBundleShortVersionString</key><string>{Esc(version)}</string>");
        b.AppendLine($"  <key>CFBundleVersion</key><string>{Esc(version)}</string>");
        b.AppendLine("  <key>LSUIElement</key><true/>");
        b.AppendLine("  <key>LSBackgroundOnly</key><true/>");
        b.AppendLine("</dict>");
        b.AppendLine("</plist>");
        return b.ToString();
    }

    /// <summary>
    /// The Windows logon task as XML (the flags of <c>schtasks /Create</c> cannot set a restart on failure): at this user's logon, least privilege, started
    /// again after a failure, no execution time limit, and the windowless build as the action.
    /// </summary>
    public static string RenderLogonTask(StandaloneLayout l)
    {
        ArgumentNullException.ThrowIfNull(l);
        if (l.AutostartBinary.Length < 4 || !char.IsAsciiLetter(l.AutostartBinary[0]) || l.AutostartBinary[1] != ':') throw new ArgumentException("the binary must be an absolute Windows path", nameof(l));
        var user = Esc(l.User);
        var b = new StringBuilder();
        b.AppendLine("""<?xml version="1.0" encoding="UTF-16"?>""");
        b.AppendLine("""<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">""");
        b.AppendLine("  <RegistrationInfo><Description>aipod agent (standalone, this user)</Description></RegistrationInfo>");
        b.AppendLine($"  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>");
        b.AppendLine($"  <Principals><Principal id=\"Author\"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>");
        b.AppendLine("  <Settings>");
        b.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
        b.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
        b.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
        b.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
        b.AppendLine("    <RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure>");
        b.AppendLine("    <Hidden>true</Hidden>");
        b.AppendLine("  </Settings>");
        b.AppendLine("  <Actions Context=\"Author\">");
        b.AppendLine($"    <Exec><Command>{Esc(l.AutostartBinary)}</Command><Arguments>daemon</Arguments></Exec>");
        b.AppendLine("  </Actions>");
        b.AppendLine("</Task>");
        return b.ToString();
    }

    private static string Esc(string value)
    {
        XmlConvert.VerifyXmlChars(value);
        return SecurityElement.Escape(value)!;
    }
}
