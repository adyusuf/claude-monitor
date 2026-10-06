using System.Text.RegularExpressions;

namespace ClaudeMonitor.Agent.Install;

/// <summary>
/// The sc.exe, icacls and reg arguments of the Windows service (ADR-0004), as argument lists for a process start, never a
/// command line. The account is a virtual one: it needs no password and exists once the service does, so the ACLs come after sc create.
/// </summary>
public static class WindowsServiceSetup
{
    public const string Description = "Claude Monitor agent: reports this machine to Claude Monitor and runs approved remote work.";
    public const string RegistryKey = @"HKLM\SYSTEM\CurrentControlSet\Services\" + ServiceLayout.ServiceName;
    public const string RestartActions = "restart/10000/restart/60000//";
    public const string ResetSeconds = "86400";

    // SYSTEM by SID, so the grant does not depend on the display language
    private const string SystemSid = "*S-1-5-18";
    private const string FullControl = "(OI)(CI)F";
    private const string ReadExecute = "(OI)(CI)RX";
    private const string EnvironmentSeparator = "|";

    // a drive path with nothing sc, icacls or reg would read as an option, wildcard, quote or separator
    private static readonly Regex SafePath = new(@"^[A-Za-z]:\\[^\x00-\x1f""|<>*?]+\z", RegexOptions.CultureInvariant);

    /// <summary>Null when the layout is safe to pass to the tools; otherwise why not.</summary>
    public static string? Check(ServiceLayout l)
    {
        ArgumentNullException.ThrowIfNull(l);
        foreach (var (what, path) in new[] { ("binary", l.Binary), ("binary folder", l.BinaryDir), ("home", l.Home) })
        {
            if (!SafePath.IsMatch(path) || path.Split('\\').Contains("..")) return $"the {what} path \"{path}\" is not a plain absolute drive path; it is refused";
        }

        return null;
    }

    /// <summary>sc create: the binPath value carries its own quotes, which is how sc reads a path with spaces.</summary>
    public static IReadOnlyList<string> Create(ServiceLayout l)
    {
        ArgumentNullException.ThrowIfNull(l);
        return ["create", ServiceLayout.ServiceName, "binPath=", $"\"{l.Binary}\" daemon", "obj=", l.Account, "start=", "delayed-auto"];
    }

    public static IReadOnlyList<string> Failure() =>
        ["failure", ServiceLayout.ServiceName, "reset=", ResetSeconds, "actions=", RestartActions];

    public static IReadOnlyList<string> SidType() => ["sidtype", ServiceLayout.ServiceName, "unrestricted"];
    public static IReadOnlyList<string> Describe() => ["description", ServiceLayout.ServiceName, Description];
    public static IReadOnlyList<string> Start() => ["start", ServiceLayout.ServiceName];
    public static IReadOnlyList<string> Stop() => ["stop", ServiceLayout.ServiceName];
    public static IReadOnlyList<string> Delete() => ["delete", ServiceLayout.ServiceName];
    public static IReadOnlyList<string> Query() => ["query", ServiceLayout.ServiceName];

    /// <summary>The service's environment: one REG_MULTI_SZ value under the service key (a service has no other place for it).</summary>
    public static IReadOnlyList<string> Environment(ServiceLayout l)
    {
        ArgumentNullException.ThrowIfNull(l);
        var data = string.Join(EnvironmentSeparator, l.ServiceEnvironment.Select(e => $"{e.Name}={e.Value}"));
        return ["add", RegistryKey, "/v", "Environment", "/t", "REG_MULTI_SZ", "/s", EnvironmentSeparator, "/d", data, "/f"];
    }

    /// <summary>The home: full control for the service account and SYSTEM, nothing inherited, nobody else.</summary>
    public const string AdministratorsSid = "*S-1-5-32-544";
    public const string ReadOnly = "(OI)(CI)R";

    public static IReadOnlyList<string> HomeAcl(ServiceLayout l)
    {
        ArgumentNullException.ThrowIfNull(l);
        // Administrators may read (the login code, the log) but not write; the service account and SYSTEM own it.
        return [l.Home, "/inheritance:r", "/grant:r", $"{l.Account}:{FullControl}", $"{SystemSid}:{FullControl}", $"{AdministratorsSid}:{ReadOnly}"];
    }

    /// <summary>The binary folder keeps its inherited admin-only write; the service account only gets read and execute.</summary>
    public static IReadOnlyList<string> BinaryAcl(ServiceLayout l)
    {
        ArgumentNullException.ThrowIfNull(l);
        return [l.BinaryDir, "/grant", $"{l.Account}:{ReadExecute}"];
    }
}
