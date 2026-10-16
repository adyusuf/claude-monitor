using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeMonitor.Agent.Capture;

/// <summary>The branch, the commit and the tag that points at it, as read from a checkout's files.</summary>
public sealed record GitHeadFacts(string? Branch, string? HeadSha, string? Tag);

/// <summary>
/// Reads a checkout's current state by parsing its <c>.git</c> files (<c>HEAD</c>, <c>refs/</c>, <c>packed-refs</c>) and never by running
/// <c>git</c>: a repository's own configuration (<c>core.fsmonitor</c>, hooks) can make a <c>git</c> invocation run code the repository
/// chose, and the daemon runs in the background (ADR-0008 §3a′). Every file is read up to a small size, a link is not followed, and a value
/// that is not what the format says (a commit that is not hex, a ref name with odd characters) is dropped. A loose annotated tag cannot be
/// peeled without the object store, so only lightweight tags and packed, peeled ones are found.
/// </summary>
public static partial class GitFacts
{
    public const int FileMax = 4096;
    public const int PackedRefsMax = 1024 * 1024;
    public const int TagFilesMax = 2000;

    public static GitHeadFacts? Read(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            var dotGit = Path.Combine(folder, ".git");
            var gitDir = Directory.Exists(dotGit) ? dotGit : GitDirFromFile(folder, dotGit);
            if (gitDir is null || !Directory.Exists(gitDir)) return null;
            var common = Common(gitDir);
            var head = ReadSmall(Path.Combine(gitDir, "HEAD"))?.Trim();
            if (head is null) return null;
            string? branch = null;
            string? sha;
            if (head.StartsWith("ref: ", StringComparison.Ordinal))
            {
                var reference = head[5..].Trim();
                if (!RefName().IsMatch(reference)) return null;
                branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : null;
                sha = ResolveRef(gitDir, common, reference);
            }
            else
            {
                sha = Sha().IsMatch(head) ? head : null;
            }

            return new GitHeadFacts(branch, sha, sha is null ? null : TagAt(common, sha));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? GitDirFromFile(string folder, string dotGit)
    {
        var pointer = ReadSmall(dotGit)?.Trim();
        if (pointer is null || !pointer.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
        return Path.GetFullPath(Path.Combine(folder, pointer["gitdir:".Length..].Trim()));
    }

    private static string Common(string gitDir) =>
        ReadSmall(Path.Combine(gitDir, "commondir"))?.Trim() is { Length: > 0 } rel ? Path.GetFullPath(Path.Combine(gitDir, rel)) : gitDir;

    private static string? ResolveRef(string gitDir, string common, string reference)
    {
        foreach (var dir in new[] { gitDir, common }.Distinct())
        {
            var loose = ReadSmall(Path.Combine(dir, reference))?.Trim();
            if (loose is not null && Sha().IsMatch(loose)) return loose;
        }

        return PackedRefs(common).FirstOrDefault(p => p.Name == reference).Sha;
    }

    private static string? TagAt(string common, string sha)
    {
        var tagsDir = Path.Combine(common, "refs", "tags");
        var found = new List<string>();
        if (Directory.Exists(tagsDir))
        {
            foreach (var file in Directory.EnumerateFiles(tagsDir, "*", SearchOption.AllDirectories).Take(TagFilesMax))
            {
                if (ReadSmall(file)?.Trim() != sha) continue;
                found.Add(Path.GetRelativePath(tagsDir, file).Replace('\\', '/'));
            }
        }

        found.AddRange(PackedRefs(common).Where(p => p.Name.StartsWith("refs/tags/", StringComparison.Ordinal) && (p.Sha == sha || p.Peeled == sha))
            .Select(p => p.Name["refs/tags/".Length..]));
        return found.Where(t => RefName().IsMatch("refs/tags/" + t)).OrderBy(t => t, StringComparer.Ordinal).LastOrDefault();
    }

    private static IEnumerable<(string Name, string Sha, string? Peeled)> PackedRefs(string common)
    {
        var path = Path.Combine(common, "packed-refs");
        if (IsLink(path) || !File.Exists(path) || new FileInfo(path).Length > PackedRefsMax) yield break;
        string? name = null, sha = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '^')
            {
                if (name is not null && sha is not null && Sha().IsMatch(line[1..])) yield return (name, sha, line[1..]);
                name = null;
                sha = null;
                continue;
            }

            if (name is not null && sha is not null) yield return (name, sha, null);
            var parts = line.Split(' ', 2);
            (name, sha) = parts.Length == 2 && Sha().IsMatch(parts[0]) && RefName().IsMatch(parts[1]) ? (parts[1], parts[0]) : (null, null);
        }

        if (name is not null && sha is not null) yield return (name, sha, null);
    }

    private static string? ReadSmall(string path)
    {
        if (IsLink(path) || !File.Exists(path) || new FileInfo(path).Length > FileMax) return null;
        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    [GeneratedRegex("^[0-9a-f]{40}([0-9a-f]{24})?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Sha();

    [GeneratedRegex(@"^refs/(?!.*\.\.)[A-Za-z0-9._/@+-]{1,200}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RefName();
}
