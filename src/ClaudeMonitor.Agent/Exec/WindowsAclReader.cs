using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>
/// Reads the owner and the access rules of an executable and of every folder above it, for <see cref="WindowsAclVerdict"/>.
/// Nothing here decides anything. Not run on a Windows host yet (ADR-0005, "Unverified platforms").
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsAclReader
{
    private const AccessControlSections Sections = AccessControlSections.Owner | AccessControlSections.Access;

    /// <summary>The executable first, then its folder and every folder up to the drive root; null when any of them cannot be read.</summary>
    public static IReadOnlyList<PathAcl>? ReadChain(string realExe)
    {
        try
        {
            var chain = new List<PathAcl> { Snapshot(new FileInfo(realExe).GetAccessControl(Sections)) };
            for (var dir = Path.GetDirectoryName(realExe); dir is not null; dir = Path.GetDirectoryName(dir))
            {
                chain.Add(Snapshot(new DirectoryInfo(dir).GetAccessControl(Sections)));
            }

            return chain;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or SecurityException)
        {
            return null; // unreadable is unknown, and unknown is untrusted
        }
    }

    private static PathAcl Snapshot(FileSystemSecurity security)
    {
        // Asking for SecurityIdentifier keeps the SIDs as they are stored, with no name lookup that could fail or lie.
        var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value ?? string.Empty;
        var rules = new List<AclRule>();
        foreach (var rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            // A rule of a kind this does not know cannot be judged: the whole read fails rather than skip it.
            if (rule is not FileSystemAccessRule r)
            {
                throw new InvalidOperationException("Unexpected access rule type.");
            }

            rules.Add(new AclRule(
                r.IdentityReference.Value,
                (int)r.FileSystemRights,
                r.AccessControlType != AccessControlType.Deny, // anything that is not a deny counts as an allow
                (r.PropagationFlags & PropagationFlags.InheritOnly) != 0));
        }

        return new PathAcl(owner, rules);
    }
}
