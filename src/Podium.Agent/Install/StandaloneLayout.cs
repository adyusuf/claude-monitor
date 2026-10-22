using Podium.Contracts;

namespace Podium.Agent.Install;

/// <summary>The names a standalone autostart is registered under (ADR-0016): the launchd label, the systemd user unit, the logon task and the application bundle.</summary>
public sealed record StandaloneNames(string Label, string UnitName, string TaskName, string BundleName, string BundleId)
{
    /// <summary>The label is not reverse-DNS (no domain of ours is known, as ADR-0012 did for the tray).</summary>
    public static readonly StandaloneNames Default = new(StandaloneLayout.Label, StandaloneLayout.UnitName, StandaloneLayout.TaskName, StandaloneLayout.BundleName, "aipod.agent.app");
}

/// <summary>
/// Where a standalone agent lives (ADR-0008 §3g): everything in the user's own home and account, nothing admin-owned. The agent home keeps a copy of the
/// binary; on macOS the autostart runs the executable inside an application bundle (an app, not a console program: no window, no Dock icon), on Windows
/// a windowless build of the same program (<c>aipodw.exe</c>) next to the console one (decision §9.12); on Linux the plain binary under
/// <c>systemd --user</c>. The CLI verbs keep using the console binary.
/// </summary>
public sealed record StandaloneLayout(string Os, string Home, string Binary, string AutostartBinary, string DefinitionPath, string? BundlePath, string User)
{
    public const string Label = "aipod.agent.user";
    public const string UnitName = "aipod.service";
    public const string TaskName = "AipodAgent";
    public const string BundleName = "Aipod.app";
    public const string WindowlessName = "aipodw.exe";
    public const string EnvStandalone = "AP_STANDALONE";
    public const string EnvHome = "AP_AGENT_HOME";

    /// <summary>The names this layout's autostart is registered under.</summary>
    public StandaloneNames Names { get; init; } = StandaloneNames.Default;

    public static StandaloneLayout For(string os, string userProfile, string home, string user)
    {
        ArgumentException.ThrowIfNullOrEmpty(os);
        ArgumentException.ThrowIfNullOrEmpty(userProfile);
        ArgumentException.ThrowIfNullOrEmpty(home);
        ArgumentException.ThrowIfNullOrEmpty(user);
        switch (os)
        {
            case OsKinds.Windows:
                return new StandaloneLayout(os, home, $@"{home}\bin\aipod.exe", $@"{home}\bin\{WindowlessName}", $@"{home}\{TaskName}.task.xml", null, user);
            case OsKinds.MacOs:
                var bundle = $"{home}/{BundleName}";
                return new StandaloneLayout(os, home, $"{home}/bin/aipod", $"{bundle}/Contents/MacOS/aipod",
                    $"{userProfile}/Library/LaunchAgents/{Label}.plist", bundle, user);
            case OsKinds.Linux:
                return new StandaloneLayout(os, home, $"{home}/bin/aipod", $"{home}/bin/aipod", $"{userProfile}/.config/systemd/user/{UnitName}", null, user);
            default:
                throw new ArgumentOutOfRangeException(nameof(os), os, "a standalone agent runs on macOS, Windows or Linux");
        }
    }

    /// <summary>The variables the autostarted daemon needs: standalone mode on and its home.</summary>
    public IReadOnlyList<(string Name, string Value)> Environment => [(EnvStandalone, "1"), (EnvHome, Home)];

    public string InfoPlistPath => BundlePath is null ? "" : $"{BundlePath}/Contents/Info.plist";
}
