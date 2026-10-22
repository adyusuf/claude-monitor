using System.Text.RegularExpressions;

namespace Podium.Agent.Install;

/// <summary>
/// The sc.exe, icacls and reg arguments of the Windows service (ADR-0005), as argument lists for a process start, never a
/// command line. The account is a virtual one: it needs no password and exists once the service does, so the ACLs come after sc create.
/// </summary>
public static class WindowsServiceSetup
{
    /// <summary>What the Services list shows as the name; the service name itself (the identifier) is unchanged (ADR-0013, tier 1).</summary>
    public const string DisplayName = "aipod agent";

    public const string Description = "aipod agent: reports this machine to aipod and runs approved remote work.";
    public const string RegistryKey = @"HKLM\SYSTEM\CurrentControlSet\Services\" + ServiceLayout.ServiceName;

    public static string RegistryKeyOf(string name) => @"HKLM\SYSTEM\CurrentControlSet\Services\" + name;
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
        return ["create", l.Names.ServiceName, "binPath=", $"\"{l.Binary}\" daemon", "obj=", l.Account, "start=", "delayed-auto", "DisplayName=", DisplayName];
    }

    // The service name is the old one unless the caller names another (a service made since the rename, ADR-0013 tier 2).
    public static IReadOnlyList<string> Failure(string name = ServiceLayout.ServiceName) =>
        ["failure", name, "reset=", ResetSeconds, "actions=", RestartActions];

    public static IReadOnlyList<string> SidType(string name = ServiceLayout.ServiceName) => ["sidtype", name, "unrestricted"];
    public static IReadOnlyList<string> Describe(string name = ServiceLayout.ServiceName) => ["description", name, Description];
    public static IReadOnlyList<string> Start(string name = ServiceLayout.ServiceName) => ["start", name];
    public static IReadOnlyList<string> Stop(string name = ServiceLayout.ServiceName) => ["stop", name];
    public static IReadOnlyList<string> Delete(string name = ServiceLayout.ServiceName) => ["delete", name];
    public static IReadOnlyList<string> Query(string name = ServiceLayout.ServiceName) => ["query", name];

    /// <summary>"sc config" for a service that is kept but must not start at boot (the old service after a migration), or starts at boot again.</summary>
    public static IReadOnlyList<string> StartType(string name, bool disabled) => ["config", name, "start=", disabled ? "disabled" : "delayed-auto"];

    /// <summary>The service's environment: one REG_MULTI_SZ value under the service key (a service has no other place for it).</summary>
    public static IReadOnlyList<string> Environment(ServiceLayout l)
    {
        ArgumentNullException.ThrowIfNull(l);
        var data = string.Join(EnvironmentSeparator, l.ServiceEnvironment.Select(e => $"{e.Name}={e.Value}"));
        return ["add", RegistryKeyOf(l.Names.ServiceName), "/v", "Environment", "/t", "REG_MULTI_SZ", "/s", EnvironmentSeparator, "/d", data, "/f"];
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
