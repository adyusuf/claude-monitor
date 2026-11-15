using System.Security.Cryptography;
using System.Text.Json;
using Podium.Agent.Access;
using Podium.Agent.Approvals;
using Podium.Agent.Net;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>
/// Which stored access policy applies on the machine (ADR-0020 §4 rule 3): delivered as it is where signatures are not required, and only when a
/// pinned key signed it, for this workspace, with no rollback, where they are. Whatever does not apply means the default, never "no limit".
/// </summary>
public sealed class AccessPolicyVerifierTests : IDisposable
{
    private static readonly Guid Workspace = Guid.Parse("0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e70");
    private static readonly Guid Project = Guid.Parse("0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e71");
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private const string Key = "git:git.example.invalid/team/shop";
    private static readonly AccessOsRules None = new([], [], []);

    private readonly ECDsa owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        owner.Dispose();
        stranger.Dispose();
    }

    private static string IdOf(ECDsa key) => ApprovalSigning.Fingerprint(key.ExportSubjectPublicKeyInfo());

    private ApproversPolicy Required => new(true, [new Approver(IdOf(owner), "laptop", Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo()))]);

    private static AccessPolicyDocument Doc(string folder = "/srv/app", bool cleared = false) =>
        new(AccessPolicyRules.Format, [Key], false, cleared ? None : new AccessOsRules([folder], [], []), None, None, false, cleared);

    /// <summary>A message as the API delivers it; signed by <paramref name="signer"/> for <paramref name="signedWorkspace"/> when one is given.</summary>
    private static AccessPolicyMessage Message(int version, AccessPolicyDocument? document = null, ECDsa? signer = null, Guid? workspace = null,
        Guid? signedWorkspace = null)
    {
        var doc = document ?? Doc();
        var hash = AccessPolicySigning.Hash(doc);
        var ws = workspace ?? Workspace;
        string? signature = signer is null ? null
            : Convert.ToBase64String(signer.SignData(AccessPolicySigning.Payload(signedWorkspace ?? ws, Project, version, hash), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        return new AccessPolicyMessage(Project, ws, version, doc, hash, signer is null ? null : IdOf(signer), signature);
    }

    private static string Json(AccessPolicyMessage message) => JsonSerializer.Serialize(message, ApiClient.Json);

    private static StoredAccessPolicy Row(AccessPolicyMessage? delivered, AccessPolicyMessage? kept = null, string? raw = null) =>
        new(Project.ToString("D"), Workspace.ToString("D"), delivered?.Version, raw ?? (delivered is null ? null : Json(delivered)), delivered?.Signature is not null, Now,
            kept?.Version, kept is null ? null : Json(kept));

    private static AppliedAccessPolicy One(IReadOnlyList<StoredAccessPolicy> rows, ApproversPolicy approvers) =>
        Assert.Single(AccessPolicyVerifier.Judge(rows, approvers, Workspace));

    // ---- signatures not required ------------------------------------------------------------------------------------------

    [Fact]
    public void Without_required_signatures_a_valid_delivered_policy_applies_as_it_comes()
    {
        var applied = One([Row(Message(2))], ApproversPolicy.Off);

        Assert.Equal((AccessPolicyStates.Applied, 2, false), (applied.State, applied.Version, applied.IsSigned));
        Assert.Equal(["/srv/app"], applied.Document!.Linux.AllowedFolders);
        Assert.Equal([Key], applied.Keys);
    }

    [Fact]
    public void A_message_whose_hash_is_not_its_documents_or_of_another_workspace_or_unreadable_gives_the_default_never_no_limit()
    {
        var tampered = Message(2) with { Document = Doc("/") with { IncludeProjectFolder = true } };
        var foreign = Message(2, workspace: Guid.NewGuid());

        var byHash = One([Row(tampered)], ApproversPolicy.Off);
        var byWorkspace = One([Row(foreign)], ApproversPolicy.Off);
        var unreadable = One([Row(Message(2), raw: "{ not json")], ApproversPolicy.Off);

        Assert.Equal((AccessPolicyStates.Invalid, (AccessPolicyDocument?)null), (byHash.State, byHash.Document));
        Assert.Equal((AccessPolicyStates.Invalid, (AccessPolicyDocument?)null), (byWorkspace.State, byWorkspace.Document));
        Assert.Equal((AccessPolicyStates.Invalid, (AccessPolicyDocument?)null), (unreadable.State, unreadable.Document));
        Assert.Empty(unreadable.Keys); // it names no repository, so the layers apply the default to every run
    }

    [Fact]
    public void A_cleared_policy_is_reported_as_the_default_and_still_covers_its_repositories()
    {
        var applied = One([Row(Message(3, Doc(cleared: true)))], ApproversPolicy.Off);

        Assert.Equal((AccessPolicyStates.Default, (AccessPolicyDocument?)null), (applied.State, applied.Document));
        Assert.Equal([Key], applied.Keys);
    }

    [Fact]
    public void A_row_of_another_workspace_or_with_no_workspace_known_is_not_judged_at_all()
    {
        var other = Row(Message(2)) with { WorkspaceId = Guid.NewGuid().ToString("D") };

        Assert.Empty(AccessPolicyVerifier.Judge([other], ApproversPolicy.Off, Workspace));
        Assert.Empty(AccessPolicyVerifier.Judge([Row(Message(2))], ApproversPolicy.Off, null));
    }

    // ---- signatures required ----------------------------------------------------------------------------------------------

    [Fact]
    public void With_required_signatures_only_a_policy_a_pinned_key_signed_for_this_workspace_applies()
    {
        Assert.Equal(AccessPolicyStates.Applied, One([Row(Message(2, signer: owner))], Required).State);
        Assert.Equal(AccessPolicyStates.UnsignedIgnored, One([Row(Message(2))], Required).State);
        Assert.Equal(AccessPolicyStates.KeyUnknown, One([Row(Message(2, signer: stranger))], Required).State);
        Assert.Equal(AccessPolicyStates.Invalid, One([Row(Message(2, signer: owner, signedWorkspace: Guid.NewGuid()))], Required).State);
        Assert.Null(One([Row(Message(2))], Required).Document);
    }

    [Fact]
    public void A_signature_over_another_version_does_not_verify()
    {
        var signedForTwo = Message(2, signer: owner);

        var applied = One([Row(signedForTwo with { Version = 3 })], Required);

        Assert.Equal((AccessPolicyStates.Invalid, (AccessPolicyDocument?)null), (applied.State, applied.Document));
    }

    [Fact]
    public void An_unsigned_newer_version_leaves_the_last_signed_one_in_place()
    {
        var kept = Message(2, Doc("/srv/kept"), signer: owner);

        var applied = One([Row(Message(3, Doc("/srv/loose")), kept)], Required);

        Assert.Equal((AccessPolicyStates.KeptSigned, 2), (applied.State, applied.Version));
        Assert.Equal(["/srv/kept"], applied.Document!.Linux.AllowedFolders);
    }

    [Fact]
    public void An_older_signed_version_never_replaces_the_kept_one_and_is_refused_as_a_rollback_once_the_kept_key_is_unpinned()
    {
        var older = Message(2, Doc("/srv/old"), signer: owner);
        var kept = Message(3, Doc("/srv/new"), signer: owner);

        var whileKept = One([Row(older, kept)], Required);
        var keptByStranger = Message(3, Doc("/srv/new"), signer: stranger);
        var rollback = One([Row(older, keptByStranger)], Required);

        Assert.Equal((AccessPolicyStates.KeptSigned, 3), (whileKept.State, whileKept.Version));
        Assert.Equal(["/srv/new"], whileKept.Document!.Linux.AllowedFolders);
        Assert.Equal((AccessPolicyStates.RollbackRefused, (AccessPolicyDocument?)null), (rollback.State, rollback.Document));
    }

    [Fact]
    public void A_newer_signed_version_replaces_the_kept_one()
    {
        var applied = One([Row(Message(4, Doc("/srv/new"), signer: owner), Message(3, signer: owner))], Required);

        Assert.Equal((AccessPolicyStates.Applied, 4), (applied.State, applied.Version));
    }

    [Fact]
    public void The_sync_keeps_a_message_only_when_it_verifies_and_is_newer_than_the_kept_one()
    {
        var kept = Message(3, signer: owner);

        Assert.True(AccessPolicyVerifier.Keeps(Message(4, signer: owner), kept, Required, Workspace));
        Assert.True(AccessPolicyVerifier.Keeps(Message(3, signer: owner), kept, Required, Workspace)); // the same version and hash again
        Assert.False(AccessPolicyVerifier.Keeps(Message(3, Doc("/srv/other"), signer: owner), kept, Required, Workspace));
        Assert.False(AccessPolicyVerifier.Keeps(Message(2, signer: owner), kept, Required, Workspace));
        Assert.False(AccessPolicyVerifier.Keeps(Message(4), kept, Required, Workspace));
        Assert.False(AccessPolicyVerifier.Keeps(Message(4, signer: stranger), null, Required, Workspace));
    }
}
