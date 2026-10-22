using Podium.Agent.Config;
using Podium.Contracts;

namespace Podium.Agent.Install;

/// <summary>The identifiers a boot service is known by: the service or unit name, the launchd label and the sudoers file (ADR-0016).</summary>
public sealed record ServiceNames(string ServiceName, string UnitPath, string Label, string PlistPath, string SudoersFileName)
{
    /// <summary>The names of the service: the launchd label is not reverse-DNS (no domain of ours is known, as ADR-0012 did for the tray).</summary>
    public static readonly ServiceNames Default = new(ServiceLayout.ServiceName, "/etc/systemd/system/aipod.service", "aipod.agent", "/Library/LaunchDaemons/aipod.agent.plist", "aipod-run");
}

/// <summary>
/// Where a boot service lives on one OS (ADR-0005, "Service mode"): an admin-owned binary folder, a home only the service
/// account can use, and the admin-owned exec policy file. The binary is never the copy in a user's home.
/// </summary>
public sealed record ServiceLayout(string Os, string BinaryDir, string Binary, string Home, string Account, string ExecConfigPath)
{
    public const string ServiceName = "aipod";
    public const string LinuxAccount = "aipod";
    public const string MacAccount = "_aipod";
    public const string WindowsAccount = @"NT SERVICE\aipod";

    /// <summary>The identifiers of this layout's service.</summary>
    public ServiceNames Names { get; init; } = ServiceNames.Default;

    /// <summary>The variables a service process needs: service mode on, its home, and the single-file bundle extracted inside that home.</summary>
    public const string EnvService = "AP_SERVICE";
    public const string EnvHome = "AP_AGENT_HOME";
    public const string EnvBundleDir = "DOTNET_BUNDLE_EXTRACT_BASE_DIR";
    public const string BundleFolder = ".net";

    private const string DefaultProgramFiles = @"C:\Program Files";
    private const string DefaultProgramData = @"C:\ProgramData";
    private const string MacRoot = "/Library/Application Support/aipod";

    public string BundleDir => Os == OsKinds.Windows ? $@"{Home}\{BundleFolder}" : $"{Home}/{BundleFolder}";

    public const string EnvServer = "AP_SERVER";

    /// <summary>The API's origin the service logs in to by itself (https only); null leaves AP_SERVER unset.</summary>
    public string? Server { get; init; }

    /// <summary>Where a service that is not connected yet writes its device code for an admin to read (ADR-0005).</summary>
    public string LoginCodePath => Os == OsKinds.Windows ? $@"{Home}\{LoginCodeFile}" : $"{Home}/{LoginCodeFile}";

    public const string LoginCodeFile = "login-code.txt";

    public IReadOnlyList<(string Name, string Value)> ServiceEnvironment => Server is { } server
        ? [(EnvService, "1"), (EnvHome, Home), (EnvBundleDir, BundleDir), (EnvServer, server)]
        : [(EnvService, "1"), (EnvHome, Home), (EnvBundleDir, BundleDir)];

    public static ServiceLayout For(string os) => For(os, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    internal static ServiceLayout For(string os, string programFiles, string programData)
    {
        var exec = AgentConfig.DefaultExecConfigPath(os);
        switch (os)
        {
            case OsKinds.Linux:
                return new ServiceLayout(os, "/usr/local/lib/aipod", "/usr/local/lib/aipod/aipod", "/var/lib/aipod", LinuxAccount, exec);
            case OsKinds.MacOs:
                return new ServiceLayout(os, $"{MacRoot}/bin", $"{MacRoot}/bin/aipod", $"{MacRoot}/home", MacAccount, exec);
            case OsKinds.Windows:
                var files = string.IsNullOrEmpty(programFiles) ? DefaultProgramFiles : programFiles;
                var data = string.IsNullOrEmpty(programData) ? DefaultProgramData : programData;
                return new ServiceLayout(os, $@"{files}\aipod", $@"{files}\aipod\aipod.exe", $@"{data}\aipod\service", WindowsAccount, exec);
            default:
                throw new ArgumentOutOfRangeException(nameof(os), os, "no service layout for this OS");
        }
    }
}
