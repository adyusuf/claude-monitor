namespace ClaudeMonitor.Agent.Exec;

/// <summary>
/// One access rule of a path, as read from its ACL: who, which rights, allow or deny, whether it only applies to children, and
/// whether it is inherited by files (ObjectInherit), which is what makes an inherit-only rule reach the files beside an executable.
/// </summary>
internal readonly record struct AclRule(string Sid, int Rights, bool Allow, bool InheritOnly, bool ObjectInherit = false);

/// <summary>The owner and the access rules of one path (a file or a folder), as SID strings.</summary>
internal sealed record PathAcl(string OwnerSid, IReadOnlyList<AclRule> Rules);

internal enum AclReason
{
    Trusted,

    /// <summary>Nothing was read, or the walk did not reach the executable's folder: fail-closed.</summary>
    NoSnapshot,

    OwnerNotAdmin,
    WriteGranted,
}

/// <summary>The decision, why, and which path of the chain decided it (0 is the executable).</summary>
internal readonly record struct AclVerdict(bool Trusted, AclReason Reason, int Index);

/// <summary>Well-known principals by SID string, so the decision does not depend on names or on a Windows host.</summary>
internal static class WindowsSids
{
    public const string Administrators = "S-1-5-32-544";
    public const string System = "S-1-5-18";
    public const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>A placeholder that only means something in an inheritable rule: it becomes the creator's SID on a child.</summary>
    public const string CreatorOwner = "S-1-3-0";

    public static bool IsAdmin(string? sid) =>
        string.Equals(sid, Administrators, StringComparison.OrdinalIgnoreCase)
        || string.Equals(sid, System, StringComparison.OrdinalIgnoreCase)
        || string.Equals(sid, TrustedInstaller, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Whether a Windows program file could have been replaced by anyone but an administrator (ADR-0005). A pure decision on
/// snapshots, so it runs and is tested on any OS; <see cref="WindowsAclReader"/> builds the snapshots on Windows.
/// The chain is the executable first, then its folder, then every folder above it up to the drive root. Each path must
/// be owned by an admin principal (an owner can always rewrite the ACL) and carry no ALLOW rule that applies to the path
/// itself and grants a write-like right to anyone else. A DENY rule changes nothing: it can only remove access, so it
/// never makes a path untrusted, and it never makes an allowed write acceptable either.
/// </summary>
internal static class WindowsAclVerdict
{
    // Access mask bits, by their Win32 names (FileSystemRights has the same values but is marked Windows-only, and this
    // decision must compile and run on any OS). Write, Modify and FullControl are combinations of these bits plus read
    // bits, so the single write bits are listed and the read bits left out.
    private const int WriteData = 0x2; // FILE_WRITE_DATA, on a folder FILE_ADD_FILE
    private const int AppendData = 0x4; // FILE_APPEND_DATA, on a folder FILE_ADD_SUBDIRECTORY
    private const int WriteExtendedAttributes = 0x10;
    private const int DeleteChild = 0x40; // FILE_DELETE_CHILD (DeleteSubdirectoriesAndFiles)
    private const int WriteAttributes = 0x100;
    private const int Delete = 0x10000;
    private const int WriteDac = 0x40000; // ChangePermissions
    private const int WriteOwner = 0x80000; // TakeOwnership

    // GENERIC_ALL and GENERIC_WRITE are mapped to specific rights when an ACL is written; they are listed anyway so an
    // unmapped one cannot slip through.
    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    /// <summary>Every right that lets an account change a file or what sits in a folder.</summary>
    internal const int WriteLikeRights =
        WriteData | AppendData | WriteExtendedAttributes | DeleteChild | WriteAttributes | Delete | WriteDac | WriteOwner
        | GenericAll | GenericWrite;

    /// <summary>
    /// For a folder above the executable's own folder. Creating a file or a folder there (a drive root lets every
    /// signed-in user create folders) cannot replace the entry that leads to the program; deleting it, renaming it
    /// (DeleteChild on the parent, Delete on the folder), or changing the ACL or owner can.
    /// </summary>
    internal const int AncestorWriteRights = WriteLikeRights & ~(WriteData | AppendData);

    public static AclVerdict Evaluate(IReadOnlyList<PathAcl>? chain)
    {
        // The walk must have reached at least the executable and its folder: a shorter chain is a walk that did not happen.
        if (chain is null || chain.Count < 2)
        {
            return new AclVerdict(false, AclReason.NoSnapshot, 0);
        }

        for (var i = 0; i < chain.Count; i++)
        {
            var acl = chain[i];
            if (acl is null)
            {
                return new AclVerdict(false, AclReason.NoSnapshot, i);
            }

            if (!WindowsSids.IsAdmin(acl.OwnerSid))
            {
                return new AclVerdict(false, AclReason.OwnerNotAdmin, i);
            }

            // The executable and its own folder (a planted DLL or manifest next to it counts) use every right.
            var mask = i < 2 ? WriteLikeRights : AncestorWriteRights;
            var ownFolder = i == 1;
            if (acl.Rules is null || acl.Rules.Any(rule => GrantsWrite(rule, mask, ownFolder)))
            {
                return new AclVerdict(false, AclReason.WriteGranted, i);
            }
        }

        return new AclVerdict(true, AclReason.Trusted, -1);
    }

    /// <summary>
    /// An inherit-only rule does not apply to the path it sits on, only to what is created below it, and what is below
    /// it has its own snapshot: this is why the usual inheritable CREATOR OWNER rule (full control, inherit-only) of
    /// Program Files and a drive root's inherit-only Authenticated Users rule pass. The same SID in a rule that does
    /// apply to the path is not exempt: CREATOR OWNER is not an admin principal.
    /// One exception: the executable's own folder. Its files (a DLL or manifest beside the executable) are not in the chain,
    /// so an inherit-only rule that files inherit (ObjectInherit) applies to them and counts there, except CREATOR OWNER,
    /// which only ever becomes the account that creates a file and so grants nothing to anyone else.
    /// </summary>
    private static bool GrantsWrite(AclRule rule, int mask, bool ownFolder)
    {
        var reachesSiblings = ownFolder && rule.ObjectInherit && !string.Equals(rule.Sid, WindowsSids.CreatorOwner, StringComparison.OrdinalIgnoreCase);
        return rule.Allow && (!rule.InheritOnly || reachesSiblings) && (rule.Rights & mask) != 0 && !WindowsSids.IsAdmin(rule.Sid);
    }
}
