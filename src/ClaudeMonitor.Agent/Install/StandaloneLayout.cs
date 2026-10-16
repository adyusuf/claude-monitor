using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Install;

/// <summary>
/// Where a standalone agent lives (ADR-0008 §3g): everything in the user's own home and account, nothing admin-owned. The agent home keeps a copy of the
/// binary; on macOS the autostart runs the executable inside an application bundle (an app, not a console program: no window, no Dock icon), on Windows
/// a windowless build of the same program (<c>cm-agentw.exe</c>) next to the console one (decision §9.12); on Linux the plain binary under
/// <c>systemd --user</c>. The CLI verbs keep using the console binary.
/// </summary>
public sealed record StandaloneLayout(string Os, string Home, string Binary, string AutostartBinary, string DefinitionPath, string? BundlePath, string User)
{
    public const string Label = "com.claudemonitor.agent.user";
    public const string UnitName = "claude-monitor-agent.service";
    public const string TaskName = "ClaudeMonitorAgent";
    public const string BundleName = "ClaudeMonitorAgent.app";
    public const string WindowlessName = "cm-agentw.exe";
    public const string EnvStandalone = "CM_STANDALONE";
    public const string EnvHome = "CM_AGENT_HOME";

    public static StandaloneLayout For(string os, string userProfile, string home, string user)
    {
        ArgumentException.ThrowIfNullOrEmpty(os);
        ArgumentException.ThrowIfNullOrEmpty(userProfile);
        ArgumentException.ThrowIfNullOrEmpty(home);
        ArgumentException.ThrowIfNullOrEmpty(user);
        switch (os)
        {
            case OsKinds.Windows:
                return new StandaloneLayout(os, home, $@"{home}\bin\cm-agent.exe", $@"{home}\bin\{WindowlessName}", $@"{home}\{TaskName}.task.xml", null, user);
            case OsKinds.MacOs:
                var bundle = $"{home}/{BundleName}";
                return new StandaloneLayout(os, home, $"{home}/bin/cm-agent", $"{bundle}/Contents/MacOS/cm-agent",
                    $"{userProfile}/Library/LaunchAgents/{Label}.plist", bundle, user);
            case OsKinds.Linux:
                return new StandaloneLayout(os, home, $"{home}/bin/cm-agent", $"{home}/bin/cm-agent", $"{userProfile}/.config/systemd/user/{UnitName}", null, user);
            default:
                throw new ArgumentOutOfRangeException(nameof(os), os, "a standalone agent runs on macOS, Windows or Linux");
        }
    }

    /// <summary>The variables the autostarted daemon needs: standalone mode on and its home.</summary>
    public IReadOnlyList<(string Name, string Value)> Environment => [(EnvStandalone, "1"), (EnvHome, Home)];

    public string InfoPlistPath => BundlePath is null ? "" : $"{BundlePath}/Contents/Info.plist";
}
