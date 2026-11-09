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

    /// <summary>
    /// Whether two repository keys name the same repository: compared after Unicode NFC and without case, because a clone URL and an
    /// application's URL may differ in either (github.com/Org/Shop and github.com/org/shop are one repository). Every match of a key
    /// against an access policy (ADR-0020) goes through this; stored keys keep their spelling.
    /// </summary>
    public static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Fold(a), Fold(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The comparer of <see cref="Same"/>, for sets and dictionaries of keys.</summary>
    public static IEqualityComparer<string> Comparer { get; } = new KeyComparer();

    private static string Fold(string key) => key.IsNormalized(System.Text.NormalizationForm.FormC) ? key : key.Normalize(System.Text.NormalizationForm.FormC);

    private sealed class KeyComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => (x is null && y is null) || Same(x, y);

        public int GetHashCode(string obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(Fold(obj));
    }

    [GeneratedRegex(@"^(?:[\w.\-]+@)?(?<host>[\w.\-]+):(?!//)(?<path>.+)$")]
    private static partial Regex ScpLike();
}
