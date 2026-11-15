using System.Text;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// What an access policy's hash covers and what is signed for it (ADR-0020 §4 rule 3): every field moves the hash, null and empty differ, and the
/// signed payload binds the workspace, the project and the version. The byte-for-byte vector shared with the web is in AccessPolicySigningVectorTests.
/// </summary>
public sealed class AccessPolicyEncodingTests
{
    private static readonly AccessOsRules None = new([], [], []);
    private static readonly AccessPolicyDocument Base = new(AccessPolicyRules.Format, ["git:host/team/shop"], true,
        new AccessOsRules(["/srv/app"], ["/srv/app/secret"], [new("/usr/bin/tail", null)]), None, None, false, false);

    public static TheoryData<string, AccessPolicyDocument> Changes() => new()
    {
        { "format", Base with { Format = 2 } },
        { "key", Base with { ProjectKeys = ["git:host/team/shop2"] } },
        { "project folder", Base with { IncludeProjectFolder = false } },
        { "allowed", Base with { Linux = Base.Linux with { AllowedFolders = ["/srv/other"] } } },
        { "denied", Base with { Linux = Base.Linux with { DeniedFolders = [] } } },
        { "application", Base with { Linux = Base.Linux with { Applications = [new("/usr/bin/head", null)] } } },
        { "sha256 given", Base with { Linux = Base.Linux with { Applications = [new("/usr/bin/tail", new string('a', 64))] } } },
        { "section moved to macOS", Base with { Linux = None, MacOs = Base.Linux } },
        { "section moved to Windows", Base with { Linux = None, Windows = Base.Linux } },
        { "shell", Base with { Shell = true } },
        { "cleared", Base with { Cleared = true } },
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public void Every_field_of_the_document_moves_its_hash(string field, AccessPolicyDocument changed)
    {
        Assert.True(AccessPolicySigning.Hash(Base) != AccessPolicySigning.Hash(changed), field);
        Assert.Matches("^[0-9a-f]{64}$", AccessPolicySigning.Hash(changed));
    }

    [Fact]
    public void A_null_sha256_and_an_empty_one_encode_differently_and_the_same_document_always_encodes_the_same()
    {
        var empty = Base with { Linux = Base.Linux with { Applications = [new("/usr/bin/tail", "")] } };

        Assert.NotEqual(AccessPolicySigning.Encode(Base), AccessPolicySigning.Encode(empty));
        Assert.Equal(AccessPolicySigning.Encode(Base), AccessPolicySigning.Encode(Base with { ProjectKeys = [.. Base.ProjectKeys] }));
    }

    [Fact]
    public void The_encoding_starts_with_the_hash_tag_length_prefixed()
    {
        var bytes = AccessPolicySigning.Encode(Base);
        var tag = Encoding.UTF8.GetBytes(AccessPolicySigning.HashTag);

        Assert.Equal(1, bytes[0]); // present
        Assert.Equal(tag.Length, (bytes[1] << 24) | (bytes[2] << 16) | (bytes[3] << 8) | bytes[4]);
        Assert.Equal(tag, bytes[5..(5 + tag.Length)]);
    }

    [Fact]
    public void The_signed_payload_is_the_tag_the_workspace_the_project_the_version_and_the_hash_one_per_line()
    {
        var workspace = Guid.Parse("0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e70");
        var project = Guid.Parse("0198F1C2-7A55-7D3A-9B1E-2F6C3A4D5E71");

        var payload = Encoding.UTF8.GetString(AccessPolicySigning.Payload(workspace, project, 12, "ab"));

        Assert.Equal("ap-access-policy-v1\n0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e70\n0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e71\n12\nab", payload);
        Assert.NotEqual(AccessPolicySigning.Tag, ApprovalSigning.Tag); // neither signature stands for the other
    }
}
