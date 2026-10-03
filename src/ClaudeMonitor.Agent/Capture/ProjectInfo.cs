using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeMonitor.Agent.Capture;

/// <summary>
/// Which project a session runs in, read from the git checkout around its working directory without running git:
/// the key is the normalised origin URL (so the same repository on two machines is one project), the name its last
/// segment, the branch from HEAD. Outside git, the key is a hash of the folder path: the path itself (which carries
/// the OS user name) never leaves the machine.
/// </summary>
public sealed partial record ProjectInfo(string Key, string Name, string? Branch)
{
    public static ProjectInfo? Resolve(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        var dir = new DirectoryInfo(cwd);
        for (var d = dir; d is not null; d = d.Parent)
        {
            var dotGit = Path.Combine(d.FullName, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
            {
                return FromCheckout(d.FullName, dotGit);
            }
        }

        return new ProjectInfo("dir:" + Hash(dir.FullName), dir.Name, null);
    }

    private static ProjectInfo FromCheckout(string root, string dotGit)
    {
        var gitDir = dotGit;
        if (File.Exists(dotGit) && File.ReadAllText(dotGit).Trim() is var pointer && pointer.StartsWith("gitdir:", StringComparison.Ordinal))
        {
            gitDir = Path.GetFullPath(Path.Combine(root, pointer["gitdir:".Length..].Trim()));
        }

        var common = gitDir;
        var commonFile = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonFile))
        {
            common = Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonFile).Trim()));
        }

        var url = OriginUrl(Path.Combine(common, "config"));
        var normalized = url is null ? null : NormalizeRemote(url);
        var name = normalized is null ? new DirectoryInfo(root).Name : normalized[(normalized.LastIndexOf('/') + 1)..];
        var key = normalized is null ? "dir:" + Hash(root) : "git:" + normalized;
        return new ProjectInfo(key, name, HeadBranch(Path.Combine(gitDir, "HEAD")));
    }

    internal static string? OriginUrl(string configPath)
    {
        if (!File.Exists(configPath)) return null;
        var inOrigin = false;
        foreach (var raw in File.ReadLines(configPath))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) inOrigin = line.Replace(" ", "", StringComparison.Ordinal) == "[remote\"origin\"]";
            else if (inOrigin && line.StartsWith("url", StringComparison.Ordinal) && line.Contains('=', StringComparison.Ordinal))
            {
                return line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>"git@GitHub.com:Org/Repo.git" and "https://user:pw@github.com/Org/Repo" both become "github.com/Org/Repo".</summary>
    internal static string? NormalizeRemote(string url)
    {
        var m = ScpLike().Match(url);
        string host, path;
        if (m.Success)
        {
            (host, path) = (m.Groups["host"].Value, m.Groups["path"].Value);
        }
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
        {
            (host, path) = (uri.Host, uri.AbsolutePath);
        }
        else
        {
            return null;
        }

        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        return path.Length == 0 ? null : $"{host.ToLowerInvariant()}/{path}";
    }

    internal static string? HeadBranch(string headPath)
    {
        if (!File.Exists(headPath)) return null;
        var head = File.ReadAllText(headPath).Trim();
        const string prefix = "ref: refs/heads/";
        return head.StartsWith(prefix, StringComparison.Ordinal) ? head[prefix.Length..] : head.Length >= 7 ? head[..7] : null;
    }

    private static string Hash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..16];

    [GeneratedRegex(@"^(?:[\w.\-]+@)?(?<host>[\w.\-]+):(?!//)(?<path>.+)$")]
    private static partial Regex ScpLike();
}
