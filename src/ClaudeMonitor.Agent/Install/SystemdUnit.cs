using System.Text.RegularExpressions;

namespace ClaudeMonitor.Agent.Install;

/// <summary>
/// The systemd unit of the Linux service (ADR-0005). Paths are written unquoted, so a path that would need quoting or
/// carries a systemd specifier is refused rather than escaped. ProtectHome=yes hides /home from the service: a grant
/// that points into /home is refused by the agent anyway, and the service never reads a user's files.
/// </summary>
public static class SystemdUnit
{
    public const string UnitPath = "/etc/systemd/system/cm-agent.service";
    public const string RunDir = "/run/systemd/system";
    public const string NoLoginShell = "/usr/sbin/nologin";
    public const string Description = "Claude Monitor agent";

    // at least two segments, no space, %, $, quote, backslash, semicolon or newline (\z: $ would allow a trailing newline)
    private static readonly Regex SafePath = new(@"^(/[A-Za-z0-9._+@-]+){2,}\z", RegexOptions.CultureInvariant);
    private static readonly Regex SafeAccount = new(@"^[a-z_][a-z0-9_-]{0,31}\z", RegexOptions.CultureInvariant);

    /// <summary>Null when the layout can be written into a unit; otherwise why not.</summary>
    public static string? Check(ServiceLayout l)
    {
        ArgumentNullException.ThrowIfNull(l);
        foreach (var (what, path) in new[] { ("binary", l.Binary), ("home", l.Home) })
        {
            if (!SafePath.IsMatch(path) || path.Split('/').Contains("..")) return $"the {what} path \"{path}\" needs quoting or is not absolute; it is refused";
        }

        return SafeAccount.IsMatch(l.Account) ? null : $"the account name \"{l.Account}\" is not a plain system account name";
    }

    public static string Render(ServiceLayout l)
    {
        if (Check(l) is { } problem) throw new ArgumentException(problem, nameof(l));
        List<string> lines =
        [
            "[Unit]",
            $"Description={Description}",
            "After=network-online.target",
            "Wants=network-online.target",
            "",
            "[Service]",
            "Type=simple",
            $"User={l.Account}",
            $"Group={l.Account}",
            $"ExecStart={l.Binary} daemon",
        ];
        lines.AddRange(l.ServiceEnvironment.Select(e => $"Environment={e.Name}={e.Value}"));
        lines.AddRange(
        [
            "Restart=on-failure",
            "RestartSec=10",
            "RestartPreventExitStatus=1",
            "KillMode=control-group",
            "NoNewPrivileges=yes",
            "ProtectSystem=strict",
            "ProtectHome=yes",
            $"ReadWritePaths={l.Home}",
            "PrivateTmp=yes",
            "PrivateDevices=yes",
            "CapabilityBoundingSet=",
            "AmbientCapabilities=",
            "RestrictSUIDSGID=yes",
            "LockPersonality=yes",
            "UMask=0077",
            "Nice=10",
            "IOSchedulingClass=idle",
            "CPUQuota=50%",
            "MemoryMax=1G",
            "TasksMax=256",
            "",
            "[Install]",
            "WantedBy=multi-user.target",
        ]);
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>A locked system account with its own group, no login shell and a home it does not get created with.</summary>
    public static IReadOnlyList<string> UserAddArgs(ServiceLayout l)
    {
        ArgumentNullException.ThrowIfNull(l);
        return ["--system", "--user-group", "--home-dir", l.Home, "--no-create-home", "--shell", NoLoginShell, l.Account];
    }
}
