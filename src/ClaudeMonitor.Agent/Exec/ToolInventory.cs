using System.Diagnostics;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>One place a tool may live: fixed per OS and per name, never looked up on the PATH. A path may end in one <c>*</c> folder segment.</summary>
public sealed record ToolCandidate(string Name, string Path);

/// <summary>
/// The tool inventory an agent reports (ADR-0008 §3a′): which of a fixed list of tools exist, where, in which version, and whether
/// each passes the ADR-0005 executable trust check. Report only: nothing here decides what may run. The agent executes
/// <c>--version</c> only for a candidate that is trusted or whose file its own account owns; any other hit is listed with the
/// version unknown, because a service agent running a binary another account can replace would hand that account its credentials.
/// </summary>
public static partial class ToolInventory
{
    public const int CandidatesMax = 64;
    private const string FixedPath = "/usr/bin:/bin";

    /// <summary>The candidates of an OS, with <c>~</c> expanded to the user's profile folder; user-owned locations included (listed, not run).</summary>
    public static IReadOnlyList<ToolCandidate> Candidates(string os, string userProfile)
    {
        ArgumentNullException.ThrowIfNull(userProfile);
        var list = new List<ToolCandidate>();
        void Add(string name, params string[] paths)
        {
            foreach (var p in paths) list.Add(new ToolCandidate(name, p.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(userProfile, p[2..]) : p));
        }

        if (os == OsKinds.Windows)
        {
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Add(ToolNames.Sh, Path.Combine(programs, "Git", "bin", "sh.exe"));
            Add(ToolNames.Bash, Path.Combine(programs, "Git", "bin", "bash.exe"));
            Add(ToolNames.PowerShell, Path.Combine(programs, "PowerShell", "7", "pwsh.exe"));
            Add(ToolNames.WindowsPowerShell, Path.Combine(system, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"));
            Add(ToolNames.Python, Path.Combine(programs, "Python3*", "python.exe"), Path.Combine(local, "Programs", "Python", "Python3*", "python.exe"));
            Add(ToolNames.Git, Path.Combine(programs, "Git", "cmd", "git.exe"));
            Add(ToolNames.Dotnet, Path.Combine(programs, "dotnet", "dotnet.exe"));
            Add(ToolNames.Node, Path.Combine(programs, "nodejs", "node.exe"));
            Add(ToolNames.Npm, Path.Combine(programs, "nodejs", "npm.cmd"));
            Add(ToolNames.Docker, Path.Combine(programs, "Docker", "Docker", "resources", "bin", "docker.exe"));
            Add(ToolNames.Psql, Path.Combine(programs, "PostgreSQL", "*", "bin", "psql.exe"));
            Add(ToolNames.Java, Path.Combine(programs, "Java", "*", "bin", "java.exe"), Path.Combine(programs, "Eclipse Adoptium", "*", "bin", "java.exe"));
            Add(ToolNames.Claude, Path.Combine(roaming, "npm", "claude.cmd"), Path.Combine(local, "Programs", "claude", "claude.exe"));
            return list;
        }

        const string Brew = "/opt/homebrew/bin/";
        const string LinuxBrew = "/home/linuxbrew/.linuxbrew/bin/";
        Add(ToolNames.Sh, "/bin/sh");
        Add(ToolNames.Bash, "/bin/bash", "/usr/bin/bash", Brew + "bash", "/usr/local/bin/bash", LinuxBrew + "bash");
        Add(ToolNames.Python, "/usr/bin/python3", "/usr/local/bin/python3", Brew + "python3", LinuxBrew + "python3", "~/.local/bin/python3");
        Add(ToolNames.PowerShell, "/usr/bin/pwsh", "/usr/local/bin/pwsh", "/usr/local/microsoft/powershell/7/pwsh", Brew + "pwsh");
        Add(ToolNames.Git, "/usr/bin/git", Brew + "git", "/usr/local/bin/git", LinuxBrew + "git");
        Add(ToolNames.Dotnet, "/usr/local/share/dotnet/dotnet", "/usr/share/dotnet/dotnet", "/usr/bin/dotnet", Brew + "dotnet", "~/.dotnet/dotnet");
        Add(ToolNames.Node, Brew + "node", "/usr/local/bin/node", "/usr/bin/node", LinuxBrew + "node", "~/.volta/bin/node", "~/.nvm/versions/node/*/bin/node");
        Add(ToolNames.Npm, Brew + "npm", "/usr/local/bin/npm", "/usr/bin/npm", LinuxBrew + "npm", "~/.volta/bin/npm", "~/.nvm/versions/node/*/bin/npm");
        Add(ToolNames.Docker, "/usr/local/bin/docker", Brew + "docker", "/usr/bin/docker", LinuxBrew + "docker");
        Add(ToolNames.Psql, "/usr/bin/psql", Brew + "psql", "/opt/homebrew/opt/libpq/bin/psql", "/usr/local/bin/psql", LinuxBrew + "psql",
            "/Applications/Postgres.app/Contents/Versions/latest/bin/psql");
        Add(ToolNames.Java, "/usr/bin/java", "/opt/homebrew/opt/openjdk/bin/java", "/usr/lib/jvm/default-java/bin/java", "/usr/local/bin/java");
        Add(ToolNames.Claude, "~/.local/bin/claude", "~/.claude/local/claude", Brew + "claude", "/usr/local/bin/claude", LinuxBrew + "claude");
        return list;
    }

    /// <summary>
    /// The inventory: for each name, the first candidate that exists (a <c>*</c> segment is expanded; the newest folder wins), its real
    /// path, whether it is trusted, and its version when it may be run. Every seam is a function so the probe is testable without
    /// touching the machine: <paramref name="exists"/>, <paramref name="resolve"/> (real path or null), <paramref name="trusted"/>,
    /// <paramref name="selfOwned"/> and <paramref name="version"/> (the first line of --version, or null).
    /// </summary>
    public static IReadOnlyList<ToolInfo> Probe(IEnumerable<ToolCandidate> candidates, string userProfile, Func<string, bool> exists,
        Func<string, string?> resolve, Func<string, bool> trusted, Func<string, bool> selfOwned, Func<string, string, string?> version,
        Func<string, IEnumerable<string>>? subfolders = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(trusted);
        ArgumentNullException.ThrowIfNull(selfOwned);
        ArgumentNullException.ThrowIfNull(version);
        subfolders ??= Subfolders;
        var found = new List<ToolInfo>();
        foreach (var candidate in candidates.Take(CandidatesMax))
        {
            if (found.Any(t => t.Name == candidate.Name)) continue;
            var path = Expand(candidate.Path, subfolders).FirstOrDefault(exists);
            if (path is null || resolve(path) is not { Length: > 0 } real) continue;
            var isTrusted = trusted(real);
            var v = isTrusted || selfOwned(real) ? version(candidate.Name, real) : null;
            found.Add(new ToolInfo(candidate.Name, HomeRelative(real, userProfile), v is { Length: > 0 } ? v : null, isTrusted));
        }

        return found;
    }

    /// <summary>The tools that may run a script: the trusted interpreters only (ADR-0008 §3c).</summary>
    public static IReadOnlyList<ToolInfo> Interpreters(IReadOnlyList<ToolInfo> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return [.. tools.Where(t => t.Trusted && ToolNames.Interpreters.Contains(t.Name))];
    }

    /// <summary>A path under the user's profile is reported as <c>~/…</c>, so the OS user name never leaves the machine.</summary>
    public static string HomeRelative(string path, string userProfile)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrEmpty(userProfile)) return path;
        var home = userProfile.TrimEnd('/', '\\');
        if (path.Length > home.Length && path.StartsWith(home, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && path[home.Length] is '/' or '\\')
        {
            return "~/" + path[(home.Length + 1)..].Replace('\\', '/');
        }

        return path;
    }

    /// <summary>The inventory of this machine, with the real seams (the executable trust check, the file owner, a bounded --version).</summary>
    public static IReadOnlyList<ToolInfo> Live(AgentConfig config, string os)
    {
        ArgumentNullException.ThrowIfNull(config);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Probe(Candidates(os, profile), profile, File.Exists, Resolve, real => ExecPaths.IsTrustedExecutable(real, os), SelfOwned,
            (name, real) => ReadVersion(name, real, config.ToolVersionTimeout));
    }

    /// <summary>The --version arguments per tool (Windows PowerShell has no --version).</summary>
    public static IReadOnlyList<string> VersionArgs(string name) => name switch
    {
        ToolNames.WindowsPowerShell => ["-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()"],
        _ => ["--version"],
    };

    private static IEnumerable<string> Expand(string pattern, Func<string, IEnumerable<string>> subfolders)
    {
        var star = pattern.IndexOf('*', StringComparison.Ordinal);
        if (star < 0) return [pattern];
        var head = pattern[..star];
        var parent = Path.GetDirectoryName(head) ?? "";
        var prefix = Path.GetFileName(head);
        var rest = pattern[(star + 1)..];
        var cut = rest.IndexOfAny(['/', '\\']);
        var suffix = cut < 0 ? rest : rest[..cut];
        var tail = cut < 0 ? "" : rest[cut..];
        return subfolders(parent)
            .Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(d).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => d + tail);
    }

    private static string[] Subfolders(string parent)
    {
        try
        {
            return Directory.Exists(parent) ? Directory.GetDirectories(parent) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? Resolve(string path)
    {
        try
        {
            return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the file belongs to the account the agent runs as and every folder above it is owned by that account or by root and is
    /// writable by neither group nor others (Unix), so nobody else can swap the file between the check and the start. On Windows only
    /// the trust check lets a binary run.
    /// </summary>
    private static bool SelfOwned(string real)
    {
        if (OperatingSystem.IsWindows()) return false;
        if (UnixFiles.Euid() is not { } euid) return false;
        try
        {
            for (var path = real; path is not null; path = Path.GetDirectoryName(path))
            {
                if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) return false;
                if (UnixFileInfo.TryGet(path) is not { } stat) return false;
                if (path == real ? stat.OwnerUid != euid : stat.OwnerUid != euid && stat.OwnerUid != 0) return false;
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

}
