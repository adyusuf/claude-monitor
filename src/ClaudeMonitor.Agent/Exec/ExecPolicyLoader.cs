using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>
/// Where the exec policy comes from (ADR-0004, "Four fail-closed keys"). An interactive agent takes the level the user
/// saved; a service agent reads an admin-owned file its own account cannot write. Anything doubtful is
/// <see cref="ExecPolicy.Off"/>.
/// </summary>
public static class ExecPolicyLoader
{
    private const UnixFileMode SavedFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private const string TempSuffix = ".tmp";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static ExecPolicy Load(AgentConfig config, string? identityExecLevel)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.ServiceMode)
        {
            return identityExecLevel is not null && ExecLevels.All.Contains(identityExecLevel)
                ? new ExecPolicy(identityExecLevel, [], [])
                : ExecPolicy.Off;
        }

        return ReadServiceFile(config.ExecConfigPath);
    }

    /// <summary>The policy file of a service agent, or Off when it is missing, invalid or not admin-owned.</summary>
    public static ExecPolicy ReadServiceFile(string path)
    {
        try
        {
            if (!File.Exists(path) || !IsAdminOwned(path)) return ExecPolicy.Off;
            var file = JsonSerializer.Deserialize<ExecFile>(File.ReadAllText(path), Json);
            if (file?.Level is not { } level || !ExecLevels.All.Contains(level)) return ExecPolicy.Off;
            var executables = file.AllowedExecutables ?? [];
            var roots = file.AllowedRoots ?? [];
            return AllAbsolute(executables) && AllAbsolute(roots) ? new ExecPolicy(level, executables, roots) : ExecPolicy.Off;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // a policy that cannot be read grants nothing
            return ExecPolicy.Off;
        }
    }

    /// <summary>Written by an admin (`cm-agent install --service --exec`): temp file and atomic rename, readable by all, never by a link.</summary>
    public static void Save(string path, ExecPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!ExecLevels.All.Contains(policy.Level)) throw new ArgumentException("unknown exec level", nameof(policy));
        if (!AllAbsolute(policy.AllowedExecutables) || !AllAbsolute(policy.AllowedRoots))
        {
            throw new ArgumentException("every allowed path must be absolute", nameof(policy));
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = path + TempSuffix;
        File.Delete(temp);
        var moved = false;
        try
        {
            var file = new ExecFile(policy.Level, [.. policy.AllowedExecutables], [.. policy.AllowedRoots]);
            File.WriteAllText(temp, JsonSerializer.Serialize(file, Json));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, SavedFileMode);
            File.Move(temp, path, overwrite: true);
            moved = true;
        }
        finally
        {
            if (!moved) File.Delete(temp);
        }
    }

    /// <summary>One line for `cm-agent status`: the level and how many entries each list has, never the paths.</summary>
    public static string Describe(ExecPolicy p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return $"exec level {p.Level}, {p.AllowedExecutables.Count} allowed executables, {p.AllowedRoots.Count} allowed roots";
    }

    /// <summary>True for root (Unix) or SYSTEM and administrators (Windows); the executor refuses to run then. Unknown counts as true.</summary>
    public static bool RunningAsRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        return UnixFiles.Euid() is not { } euid || euid == 0;
    }

    private static bool AllAbsolute(IEnumerable<string> paths) => paths.All(p => !string.IsNullOrEmpty(p) && Path.IsPathFullyQualified(p));

    /// <summary>The file must not be changeable by the account that obeys it.</summary>
    private static bool IsAdminOwned(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is null || IsLink(path) || IsLink(directory)) return false;
        return OperatingSystem.IsWindows() ? !CanWrite(path) : IsAdminOwnedOnUnix(path, directory);
    }

    private const uint RootUid = 0;

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    [UnsupportedOSPlatform("windows")]
    private static bool IsAdminOwnedOnUnix(string path, string directory)
    {
        if ((File.GetUnixFileMode(path) & UnixFiles.GroupOrOtherWrite) != 0) return false;
        if ((File.GetUnixFileMode(directory) & UnixFiles.GroupOrOtherWrite) != 0) return false;
        // Owned by root (uid 0), never by the service account: ADR-0004 keeps the exec level out of its reach.
        return UnixFiles.Euid() is { } euid && euid != RootUid
            && UnixFiles.OwnerUid(path) == RootUid
            && UnixFiles.OwnerUid(directory) == RootUid;
    }

    /// <summary>Windows: if this process can open the file for writing, the service account could change its own policy.</summary>
    private static bool CanWrite(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            // locked by someone else: cannot show it is read-only, so it is treated as writable
            return true;
        }
    }

    private sealed record ExecFile(string? Level, List<string>? AllowedExecutables, List<string>? AllowedRoots);
}
