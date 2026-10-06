using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The Windows trust decision on snapshots, so it runs on every OS. The reader that builds them is not covered here.</summary>
public sealed class WindowsAclVerdictTests
{
    // Access masks as Windows stores them (the same values as FileSystemRights, which is Windows-only to compile against).
    private const int ReadAndExecute = 0x1200A9;
    private const int Write = 0x116;
    private const int Modify = 0x1301BF;
    private const int FullControl = 0x1F01FF;
    private const int WriteData = 0x2;
    private const int AppendData = 0x4;
    private const int WriteExtendedAttributes = 0x10;
    private const int DeleteSubdirectoriesAndFiles = 0x40;
    private const int WriteAttributes = 0x100;
    private const int Delete = 0x10000;
    private const int ChangePermissions = 0x40000;
    private const int TakeOwnership = 0x80000;
    private const int GenericWrite = 0x40000000;

    private const string Users = "S-1-5-32-545";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string Everyone = "S-1-1-0";
    private const string ServiceAccount = "S-1-5-80-1111111111-2222222222-3333333333-4444444444-5555555555";
    private const string NormalUser = "S-1-5-21-1000000000-2000000000-3000000000-1001";

    private const string Exe = "exe";
    private const string Folder = "folder";
    private const string Ancestor = "ancestor";
    private const string Root = "root";

    private static AclRule Allow(string sid, int rights, bool inheritOnly = false) => new(sid, rights, true, inheritOnly);

    private static AclRule Deny(string sid, int rights) => new(sid, rights, false, false);

    /// <summary>What Program Files looks like: the system principals in control, Users read and run, CREATOR OWNER inherit-only.</summary>
    private static PathAcl Admin(params AclRule[] extra) => new(WindowsSids.TrustedInstaller,
    [
        Allow(WindowsSids.TrustedInstaller, FullControl),
        Allow(WindowsSids.System, FullControl),
        Allow(WindowsSids.Administrators, FullControl),
        Allow(Users, ReadAndExecute),
        Allow(WindowsSids.CreatorOwner, FullControl, inheritOnly: true),
        .. extra,
    ]);

    /// <summary>exe, its folder, a folder above it and the drive root, in that order.</summary>
    private static AclVerdict Chain(PathAcl? exe = null, PathAcl? folder = null, PathAcl? ancestor = null, PathAcl? root = null) =>
        WindowsAclVerdict.Evaluate([exe ?? Admin(), folder ?? Admin(), ancestor ?? Admin(), root ?? Admin()]);

    private static AclVerdict With(string where, PathAcl acl) => where switch
    {
        Exe => Chain(exe: acl),
        Folder => Chain(folder: acl),
        Ancestor => Chain(ancestor: acl),
        Root => Chain(root: acl),
        _ => throw new ArgumentOutOfRangeException(nameof(where)),
    };

    public static TheoryData<string> Places => new() { Exe, Folder, Ancestor, Root };

    public static TheoryData<string> Principals => new() { Users, AuthenticatedUsers, Everyone, ServiceAccount, NormalUser, WindowsSids.CreatorOwner };

    [Fact]
    public void A_program_files_like_chain_is_trusted()
    {
        var v = Chain();
        Assert.True(v.Trusted);
        Assert.Equal(AclReason.Trusted, v.Reason);
    }

    [Theory]
    [InlineData(WindowsSids.Administrators)]
    [InlineData(WindowsSids.System)]
    [InlineData(WindowsSids.TrustedInstaller)]
    public void Each_admin_principal_may_own_a_path(string owner)
    {
        Assert.True(Chain(exe: new PathAcl(owner, [])).Trusted);
        Assert.True(Chain(root: new PathAcl(owner.ToLowerInvariant(), [])).Trusted);
    }

    [Theory]
    [MemberData(nameof(Places))]
    public void A_path_owned_by_anyone_else_is_untrusted(string where)
    {
        foreach (var owner in new[] { NormalUser, ServiceAccount, Users, WindowsSids.CreatorOwner, string.Empty })
        {
            var v = With(where, new PathAcl(owner, Admin().Rules));
            Assert.False(v.Trusted);
            Assert.Equal(AclReason.OwnerNotAdmin, v.Reason);
        }
    }

    [Theory]
    [InlineData(Modify)]
    [InlineData(Write)]
    [InlineData(FullControl)]
    [InlineData(WriteData)]
    [InlineData(AppendData)]
    [InlineData(WriteExtendedAttributes)]
    [InlineData(WriteAttributes)]
    [InlineData(Delete)]
    [InlineData(DeleteSubdirectoriesAndFiles)]
    [InlineData(ChangePermissions)]
    [InlineData(TakeOwnership)]
    [InlineData(GenericWrite)]
    public void A_write_like_right_for_anyone_else_makes_the_executable_and_its_folder_untrusted(int rights)
    {
        foreach (var sid in new[] { Users, AuthenticatedUsers, Everyone, ServiceAccount, NormalUser })
        {
            Assert.Equal(AclReason.WriteGranted, With(Exe, Admin(Allow(sid, rights))).Reason);
            Assert.Equal(AclReason.WriteGranted, With(Folder, Admin(Allow(sid, rights))).Reason);
        }
    }

    [Theory]
    [InlineData(Modify)]
    [InlineData(Write)]
    [InlineData(FullControl)]
    [InlineData(WriteExtendedAttributes)]
    [InlineData(WriteAttributes)]
    [InlineData(Delete)]
    [InlineData(DeleteSubdirectoriesAndFiles)]
    [InlineData(ChangePermissions)]
    [InlineData(TakeOwnership)]
    [InlineData(GenericWrite)]
    public void A_right_that_can_replace_the_path_makes_every_folder_above_untrusted(int rights)
    {
        foreach (var sid in new[] { Users, AuthenticatedUsers, Everyone, ServiceAccount })
        {
            Assert.False(With(Ancestor, Admin(Allow(sid, rights))).Trusted);
            Assert.False(With(Root, Admin(Allow(sid, rights))).Trusted);
        }
    }

    [Theory]
    [InlineData(WriteData)]
    [InlineData(AppendData)]
    [InlineData(WriteData | AppendData)]
    public void Creating_files_or_folders_above_the_executables_folder_does_not_matter_but_in_it_does(int rights)
    {
        // The default drive root lets Authenticated Users create folders; it cannot replace what is already there.
        Assert.True(With(Ancestor, Admin(Allow(AuthenticatedUsers, rights))).Trusted);
        Assert.True(With(Root, Admin(Allow(AuthenticatedUsers, rights))).Trusted);
        Assert.False(With(Folder, Admin(Allow(AuthenticatedUsers, rights))).Trusted);
        Assert.False(With(Exe, Admin(Allow(AuthenticatedUsers, rights))).Trusted);
    }

    [Theory]
    [MemberData(nameof(Principals))]
    public void Read_and_execute_for_anyone_is_fine_and_a_rule_that_applies_to_the_path_with_write_is_not(string sid)
    {
        Assert.True(Chain(exe: Admin(Allow(sid, ReadAndExecute))).Trusted);
        Assert.False(Chain(exe: Admin(Allow(sid, Modify))).Trusted);
    }

    [Fact]
    public void The_default_drive_root_with_inherit_only_modify_for_authenticated_users_is_trusted()
    {
        var root = Admin(Allow(AuthenticatedUsers, AppendData), Allow(AuthenticatedUsers, Modify, inheritOnly: true));
        Assert.True(Chain(root: root).Trusted);
    }

    [Theory]
    [MemberData(nameof(Places))]
    public void An_inherit_only_creator_owner_rule_never_counts_but_one_that_applies_to_the_path_does(string where)
    {
        // Inherit-only: it only becomes the creator's rights on a new child; the existing children have their own snapshot.
        Assert.True(With(where, Admin(Allow(WindowsSids.CreatorOwner, FullControl, inheritOnly: true))).Trusted);
        // Not inherit-only: nothing here is an admin principal, so it is refused rather than reasoned about.
        Assert.False(With(where, Admin(Allow(WindowsSids.CreatorOwner, FullControl))).Trusted);
    }

    [Theory]
    [MemberData(nameof(Places))]
    public void A_deny_rule_alone_changes_nothing_and_does_not_excuse_an_allow(string where)
    {
        Assert.True(With(where, Admin(Deny(ServiceAccount, FullControl), Deny(Everyone, Modify))).Trusted);
        Assert.False(With(where, Admin(Allow(Users, Modify), Deny(ServiceAccount, Modify))).Trusted);
    }

    [Fact]
    public void Nothing_read_a_missing_chain_and_a_chain_without_the_executables_folder_are_untrusted()
    {
        Assert.Equal(AclReason.NoSnapshot, WindowsAclVerdict.Evaluate(null).Reason);
        Assert.Equal(AclReason.NoSnapshot, WindowsAclVerdict.Evaluate([]).Reason);
        Assert.Equal(AclReason.NoSnapshot, WindowsAclVerdict.Evaluate([Admin()]).Reason);
        Assert.False(WindowsAclVerdict.Evaluate([Admin(), null!]).Trusted);
        Assert.False(WindowsAclVerdict.Evaluate([new PathAcl(WindowsSids.Administrators, null!), Admin()]).Trusted);
    }

    [Fact]
    public void The_exe_directly_under_a_drive_root_is_judged_on_both()
    {
        Assert.True(WindowsAclVerdict.Evaluate([Admin(), Admin()]).Trusted);
        Assert.False(WindowsAclVerdict.Evaluate([Admin(), Admin(Allow(Users, Modify))]).Trusted);
    }

    [Fact]
    public void The_verdict_names_the_path_that_decided_it()
    {
        var v = Chain(ancestor: Admin(Allow(Users, ChangePermissions)));
        Assert.Equal(new AclVerdict(false, AclReason.WriteGranted, 2), v);
    }

    [Fact]
    public void A_windows_target_on_a_host_that_cannot_read_ACLs_is_untrusted_and_a_reader_decides_otherwise()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.False(ExecPaths.IsTrustedExecutable("C:\\Program Files\\App\\tool.exe", OsKinds.Windows));
        Assert.True(ExecPaths.IsTrustedExecutable("C:\\Program Files\\App\\tool.exe", OsKinds.Windows, _ => [Admin(), Admin(), Admin(), Admin()]));
        Assert.False(ExecPaths.IsTrustedExecutable("C:\\Program Files\\App\\tool.exe", OsKinds.Windows, _ => [Admin(), Admin(Allow(Users, Modify))]));
        Assert.False(ExecPaths.IsTrustedExecutable("C:\\Program Files\\App\\tool.exe", OsKinds.Windows, _ => null));
    }
}
