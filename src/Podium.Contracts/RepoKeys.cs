using System.Text.RegularExpressions;

namespace Podium.Contracts;

/// <summary>
/// The key of a git repository (ADR-0008, ADR-0020): <c>git:</c> and the normalised origin URL, so the same repository on two machines is one project.
/// The agent reads it from a checkout; the API computes it from an application's repository URL; both must agree.
/// </summary>
public static partial class RepoKeys
{
    public const string GitPrefix = "git:";

    /// <summary>"git@GitHub.com:Org/Repo.git" and "https://user:pw@github.com/Org/Repo" both become "github.com/Org/Repo".</summary>
    public static string? NormalizeRemote(string url)
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

    /// <summary>The repository key of a remote URL ("git:github.com/Org/Repo"), or null when the URL is not one.</summary>
    public static string? FromRemote(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : NormalizeRemote(url) is { } normalized ? GitPrefix + normalized : null;

    [GeneratedRegex(@"^(?:[\w.\-]+@)?(?<host>[\w.\-]+):(?!//)(?<path>.+)$")]
    private static partial Regex ScpLike();
}
