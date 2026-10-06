using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Install;

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

    /// <summary>The variables a service process needs: service mode on, its home, and the single-file bundle extracted inside that home.</summary>
    public const string EnvService = "CM_SERVICE";
    public const string EnvHome = "CM_AGENT_HOME";
    public const string EnvBundleDir = "DOTNET_BUNDLE_EXTRACT_BASE_DIR";
    public const string BundleFolder = ".net";

    private const string DefaultProgramFiles = @"C:\Program Files";
    private const string DefaultProgramData = @"C:\ProgramData";
    private const string MacRoot = "/Library/Application Support/ClaudeMonitor";

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
