using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using static Podium.Contracts.CanonicalBytes;

namespace Podium.Contracts;

/// <summary>
/// What an access policy's hash covers and what an owner's browser key signs for it (ADR-0020 §4 rule 3). The browser computes both, the API and the
/// agent recompute them; the signature is checked like an approval's (<see cref="ApprovalSigning.Verify"/>), with its own tag so neither stands for the other.
/// </summary>
public static class AccessPolicySigning
{
    public const string HashTag = "ap-access-policy-hash-v1";
    public const string Tag = "ap-access-policy-v1";

    /// <summary>Lowercase hex SHA-256 of the document's length-prefixed encoding (<see cref="Encode"/>).</summary>
    public static string Hash(AccessPolicyDocument document) => Convert.ToHexStringLower(SHA256.HashData(Encode(document)));

    /// <summary>
    /// The bytes the hash covers: the tag, the format, the project keys, the project-folder flag, then for Linux, macOS and Windows in that order the
    /// allowed and denied folders and the applications (count, then path and SHA-256 of each), and last the shell and cleared flags.
    /// </summary>
    public static byte[] Encode(AccessPolicyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var buffer = new MemoryStream();
        Put(buffer, HashTag);
        PutInt(buffer, document.Format);
        PutList(buffer, document.ProjectKeys);
        PutInt(buffer, Flag(document.IncludeProjectFolder));
        foreach (var rules in new[] { document.Linux, document.MacOs, document.Windows })
        {
            PutList(buffer, rules.AllowedFolders);
            PutList(buffer, rules.DeniedFolders);
            PutInt(buffer, rules.Applications.Count);
            foreach (var app in rules.Applications)
            {
                Put(buffer, app.Path);
                Put(buffer, app.Sha256);
            }
        }

        PutInt(buffer, Flag(document.Shell));
        PutInt(buffer, Flag(document.Cleared));
        return buffer.ToArray();
    }

    /// <summary>The exact bytes signed: the tag, the workspace, the project, the version and the document's hash, one per line.</summary>
    public static byte[] Payload(Guid workspaceId, Guid projectId, int version, string hash) =>
        Encoding.UTF8.GetBytes($"{Tag}\n{workspaceId:D}\n{projectId:D}\n{version.ToString(CultureInfo.InvariantCulture)}\n{hash}");

    /// <summary>True when the signature (IEEE P1363, 64 bytes) signs this version of the policy under the key.</summary>
    public static bool Verify(ReadOnlySpan<byte> spki, Guid workspaceId, Guid projectId, int version, string hash, ReadOnlySpan<byte> signature) =>
        ApprovalSigning.Verify(spki, Payload(workspaceId, projectId, version, hash), signature);

    private static int Flag(bool value) => value ? 1 : 0;
}
