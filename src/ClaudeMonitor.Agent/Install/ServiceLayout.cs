using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Install;

/// <summary>The identifiers a boot service is known by: the service or unit name, the launchd label and the sudoers file (ADR-0013, tier 2).</summary>
public sealed record ServiceNames(string ServiceName, string UnitPath, string Label, string PlistPath, string SudoersFileName)
{
    /// <summary>The names every service installed before the rename has (cm-agent, com.claudemonitor.agent): kept for good, so an old service can always be found, stopped and removed.</summary>
    public static readonly ServiceNames Legacy = new(ServiceLayout.ServiceName, SystemdUnit.UnitPath, LaunchdDaemon.Label, LaunchdDaemon.PlistPath, "cm-agent-run");

    /// <summary>The names of a service made by `aipod service migrate` or by a fresh install since: the launchd label is not reverse-DNS (no domain of ours is known, as ADR-0012 did for the tray).</summary>
    public static readonly ServiceNames Aipod = new(ServiceLayout.AipodServiceName, "/etc/systemd/system/aipod.service", "aipod.agent", "/Library/LaunchDaemons/aipod.agent.plist", "aipod-run");
}

/// <summary>
/// Where a boot service lives on one OS (ADR-0005, "Service mode"): an admin-owned binary folder, a home only the service
/// account can use, and the admin-owned exec policy file. The binary is never the copy in a user's home.
/// </summary>
public sealed record ServiceLayout(string Os, string BinaryDir, string Binary, string Home, string Account, string ExecConfigPath)
{
    public const string ServiceName = "cm-agent";
    public const string LinuxAccount = "cm-agent";
    public const string MacAccount = "_cmagent";
    public const string WindowsAccount = @"NT SERVICE\cm-agent";

    /// <summary>The same names after the migration to aipod (ADR-0013, tier 2). The binary file inside the folders keeps its name, cm-agent: the updater and the installers key on it.</summary>
    public const string AipodServiceName = "aipod";
    public const string AipodLinuxAccount = "aipod";
    public const string AipodMacAccount = "_aipod";
    public const string AipodWindowsAccount = @"NT SERVICE\aipod";

    /// <summary>The identifiers of this layout's service; the old ones unless the layout is the aipod one.</summary>
    public ServiceNames Names { get; init; } = ServiceNames.Legacy;

    /// <summary>The variables a service process needs: service mode on, its home, and the single-file bundle extracted inside that home.</summary>
    public const string EnvService = "CM_SERVICE";
    public const string EnvHome = "CM_AGENT_HOME";
    public const string EnvBundleDir = "DOTNET_BUNDLE_EXTRACT_BASE_DIR";
    public const string BundleFolder = ".net";

    private const string DefaultProgramFiles = @"C:\Program Files";
    private const string DefaultProgramData = @"C:\ProgramData";
    private const string MacRoot = "/Library/Application Support/ClaudeMonitor";
    private const string AipodMacRoot = "/Library/Application Support/aipod";

    public string BundleDir => Os == OsKinds.Windows ? $@"{Home}\{BundleFolder}" : $"{Home}/{BundleFolder}";

    public const string EnvServer = "CM_SERVER";

    /// <summary>The API's origin the service logs in to by itself (https only); null leaves CM_SERVER unset.</summary>
    public string? Server { get; init; }

    /// <summary>Where a service that is not connected yet writes its device code for an admin to read (ADR-0005).</summary>
    public string LoginCodePath => Os == OsKinds.Windows ? $@"{Home}\{LoginCodeFile}" : $"{Home}/{LoginCodeFile}";

    public const string LoginCodeFile = "login-code.txt";

    public IReadOnlyList<(string Name, string Value)> ServiceEnvironment => Server is { } server
        ? [(EnvService, "1"), (EnvHome, Home), (EnvBundleDir, BundleDir), (EnvServer, server)]
        : [(EnvService, "1"), (EnvHome, Home), (EnvBundleDir, BundleDir)];

    /// <summary>The layout of the service made since the rename (the aipod names), whether or not it exists yet.</summary>
    public static ServiceLayout ForAipod(string os) => ForAipod(os, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    internal static ServiceLayout ForAipod(string os, string programFiles, string programData)
    {
        var exec = AgentConfig.AipodExecConfigPath(os);
        switch (os)
        {
            case OsKinds.Linux:
                return new ServiceLayout(os, "/usr/local/lib/aipod", "/usr/local/lib/aipod/cm-agent", "/var/lib/aipod", AipodLinuxAccount, exec) { Names = ServiceNames.Aipod };
            case OsKinds.MacOs:
                return new ServiceLayout(os, $"{AipodMacRoot}/bin", $"{AipodMacRoot}/bin/cm-agent", $"{AipodMacRoot}/home", AipodMacAccount, exec) { Names = ServiceNames.Aipod };
            case OsKinds.Windows:
                var files = string.IsNullOrEmpty(programFiles) ? DefaultProgramFiles : programFiles;
                var data = string.IsNullOrEmpty(programData) ? DefaultProgramData : programData;
                return new ServiceLayout(os, $@"{files}\aipod", $@"{files}\aipod\cm-agent.exe", $@"{data}\aipod\service", AipodWindowsAccount, exec) { Names = ServiceNames.Aipod };
            default:
                throw new ArgumentOutOfRangeException(nameof(os), os, "no service layout for this OS");
        }
    }

    public static ServiceLayout For(string os) => For(os, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    internal static ServiceLayout For(string os, string programFiles, string programData)
    {
        var exec = AgentConfig.DefaultExecConfigPath(os);
        switch (os)
        {
            case OsKinds.Linux:
                return new ServiceLayout(os, "/usr/local/lib/cm-agent", "/usr/local/lib/cm-agent/cm-agent", "/var/lib/cm-agent", LinuxAccount, exec);
            case OsKinds.MacOs:
                return new ServiceLayout(os, $"{MacRoot}/bin", $"{MacRoot}/bin/cm-agent", $"{MacRoot}/home", MacAccount, exec);
            case OsKinds.Windows:
                var files = string.IsNullOrEmpty(programFiles) ? DefaultProgramFiles : programFiles;
                var data = string.IsNullOrEmpty(programData) ? DefaultProgramData : programData;
                return new ServiceLayout(os, $@"{files}\ClaudeMonitor", $@"{files}\ClaudeMonitor\cm-agent.exe", $@"{data}\ClaudeMonitor\service",
                    WindowsAccount, exec);
            default:
                throw new ArgumentOutOfRangeException(nameof(os), os, "no service layout for this OS");
        }
    }
}
