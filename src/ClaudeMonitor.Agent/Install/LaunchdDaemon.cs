using System.Security;
using System.Text;
using System.Xml;

namespace ClaudeMonitor.Agent.Install;

/// <summary>The LaunchDaemon of the macOS service (ADR-0004) and the dscl calls that create its hidden role account.</summary>
public static class LaunchdDaemon
{
    public const string Label = "com.claudemonitor.agent";
    public const string PlistPath = "/Library/LaunchDaemons/" + Label + ".plist";
    public const int Umask = 63; // 0077

    /// <summary>The range Apple leaves to third-party daemons; below 500, where ordinary users start.</summary>
    public const int FirstId = 200;
    public const int LastId = 399;

    private const string AccountShell = "/usr/bin/false";
    private const string AccountRealName = "Claude Monitor agent";

    public static string Render(ServiceLayout l)
    {
        ArgumentNullException.ThrowIfNull(l);
        if (!l.Binary.StartsWith('/') || !l.Home.StartsWith('/')) throw new ArgumentException("the binary and home must be absolute paths", nameof(l));
        var b = new StringBuilder();
        b.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        b.AppendLine("""<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">""");
        b.AppendLine("""<plist version="1.0">""");
        b.AppendLine("<dict>");
        b.AppendLine($"  <key>Label</key><string>{Esc(Label)}</string>");
        b.AppendLine("  <key>ProgramArguments</key>");
        b.AppendLine($"  <array><string>{Esc(l.Binary)}</string><string>daemon</string></array>");
        b.AppendLine($"  <key>UserName</key><string>{Esc(l.Account)}</string>");
        b.AppendLine($"  <key>GroupName</key><string>{Esc(l.Account)}</string>");
        b.AppendLine("  <key>EnvironmentVariables</key>");
        b.AppendLine("  <dict>");
        foreach (var (name, value) in l.ServiceEnvironment) b.AppendLine($"    <key>{Esc(name)}</key><string>{Esc(value)}</string>");
        b.AppendLine("  </dict>");
        b.AppendLine("  <key>RunAtLoad</key><true/>");
        b.AppendLine("  <key>KeepAlive</key>");
        b.AppendLine("  <dict><key>SuccessfulExit</key><false/></dict>");
        b.AppendLine($"  <key>Umask</key><integer>{Umask}</integer>");
        b.AppendLine("  <key>ProcessType</key><string>Background</string>");
        b.AppendLine("  <key>LowPriorityIO</key><true/>");
        b.AppendLine("</dict>");
        b.AppendLine("</plist>");
        return b.ToString();
    }

    /// <summary>Refuses a character XML cannot carry, then escapes the rest.</summary>
    private static string Esc(string value)
    {
        XmlConvert.VerifyXmlChars(value);
        return SecurityElement.Escape(value)!;
    }

    public static IReadOnlyList<string> AccountExistsArgs(ServiceLayout l) => [".", "-read", $"/Users/{Name(l)}"];

    public static IReadOnlyList<IReadOnlyList<string>> AccountDeleteCommands(ServiceLayout l) =>
        [[".", "-delete", $"/Users/{Name(l)}"], [".", "-delete", $"/Groups/{Name(l)}"]];

    /// <summary>The dscl calls for a hidden role account and its group, both with the same id.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> AccountCommands(ServiceLayout l, int id)
    {
        var user = $"/Users/{Name(l)}";
        var group = $"/Groups/{Name(l)}";
        var n = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return
        [
            [".", "-create", group],
            [".", "-create", group, "PrimaryGroupID", n],
            [".", "-create", user],
            [".", "-create", user, "UserShell", AccountShell],
            [".", "-create", user, "RealName", AccountRealName],
            [".", "-create", user, "UniqueID", n],
            [".", "-create", user, "PrimaryGroupID", n],
            [".", "-create", user, "NFSHomeDirectory", l.Home],
            [".", "-create", user, "IsHidden", "1"],
            [".", "-create", user, "Password", "*"],
        ];
    }

    private static string Name(ServiceLayout l)
    {
        ArgumentNullException.ThrowIfNull(l);
        return l.Account;
    }
}
