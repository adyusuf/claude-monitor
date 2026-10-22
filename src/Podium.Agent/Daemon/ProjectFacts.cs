using System.Security.Cryptography;
using System.Text;
using Podium.Agent.Auth;
using Podium.Agent.Capture;
using Podium.Agent.Config;
using Podium.Contracts;

namespace Podium.Agent.Daemon;

/// <summary>
/// What the agent reports about the project folders it was told about (ADR-0008 §3a′): the project key, the name and the branch the way a
/// session's project is keyed, the commit and the tag read from the <c>.git</c> files, and an HMAC of the folder's path. The path itself never
/// leaves the machine; the HMAC key is generated here, kept in the credential store and never uploaded, so the API cannot reverse or
/// guess a path from the value. Nothing here starts a process.
/// </summary>
public sealed class ProjectFacts(AgentConfig config, ICredentialStore? credentials = null)
{
    public const string KeyAccount = "project-hmac-key";
    private readonly ICredentialStore store = credentials ?? Credentials.For(config);

    /// <summary>
    /// The facts of the listed folders (source <c>listed</c>) and of the folders sessions worked in (<c>session</c>); a project in both is reported as listed.
    /// </summary>
    public IReadOnlyList<ProjectFact> Read(IReadOnlyList<string>? folders, IReadOnlyList<string>? visited = null, IReadOnlyDictionary<string, List<string>>? tests = null)
    {
        if ((folders is not { Count: > 0 } && visited is not { Count: > 0 }) || Key() is not { } key) return [];
        var facts = new List<ProjectFact>();
        void Add(IEnumerable<string>? paths, string source, int max)
        {
            foreach (var folder in (paths ?? []).Where(ProjectFolderList.IsAbsolute).Distinct(StringComparer.Ordinal).Take(max))
            {
                if (!Directory.Exists(folder) || ProjectInfo.Resolve(folder) is not { } info || facts.Any(f => f.Key == info.Key)) continue;
                var git = GitFacts.Read(folder);
                var test = tests is not null && tests.TryGetValue(ProjectTestCommands.Normalize(folder), out var argv) && ProjectTestCommands.IsValid(argv) ? argv : null;
                facts.Add(new ProjectFact(info.Key, info.Name, Hmac(key, Path.GetFullPath(folder)), git?.Branch ?? info.Branch, git?.HeadSha, git?.Tag, source, ClaudeTrust.IsTrusted(config, folder),
                    test, test is null ? null : ProjectTestCommands.Normalize(folder)));
            }
        }

        Add(folders, ProjectSources.Listed, ProjectFolderList.Max);
        Add(visited, ProjectSources.Session, ProjectFolderList.Max);
        return facts;
    }

    public static string Hmac(byte[] key, string path) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(path)));

    /// <summary>The per-agent key, made on first use; null when the credential store cannot hold it (then no folder fact is reported).</summary>
    private byte[]? Key()
    {
        try
        {
            if (store.Read(KeyAccount) is { Length: >= 32 } saved) return Convert.FromHexString(saved);
            var fresh = RandomNumberGenerator.GetBytes(32);
            store.Write(KeyAccount, Convert.ToHexStringLower(fresh));
            return fresh;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException
                                      or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
