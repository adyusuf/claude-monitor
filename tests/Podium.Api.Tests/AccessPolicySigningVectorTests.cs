using System.Text;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// One vector shared with web/src/lib/accessPolicyHash.test.ts (ADR-0020 §4 rule 3): the browser builds and signs a policy's hash, the API and the
/// agent recompute it, so the same document must give the same bytes on both sides. The signature was made once by Node's WebCrypto.
/// </summary>
public sealed class AccessPolicySigningVectorTests
{
    // The public half of a throwaway test key (its private half was discarded): a test vector, not a secret.
    private const string BrowserKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjAki8RDcqzMRG5/pFxCllxu5rY3N8smmesYh1PciGOvtLmcS1b7FxgGyhGzerDrnYBGknIb08ft4ivfiLNc0nQ=="; // gitleaks:allow
    private const string BrowserSignature = "grm0HT3Xlp3vyvkb5UTLi6VRV3GPYjS3lXZup9VmtxPyqpvmfaAKMI+sgk0h33lfkflnCYQF9sKnwxZfStwUew==";
    private const string Hash = "83935011bc72cf1ecd20f94d12bf92a9996a26427dce9a00791a7bfc15d98f79";
    private const int EncodedLength = 310;
    private static readonly Guid Workspace = Guid.Parse("0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e70");
    private static readonly Guid Project = Guid.Parse("0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e71");
    private static readonly string Payload = $"ap-access-policy-v1\n{Workspace:D}\n{Project:D}\n3\n{Hash}";

    private static readonly AccessPolicyDocument Document = new(AccessPolicyRules.Format, ["git:git.example.invalid/team/shop"], true,
        new AccessOsRules(["/srv/caf\u00e9"], ["/srv/caf\u00e9/secret"], [new("/usr/bin/tail", string.Concat(Enumerable.Repeat("ab", 32)))]),
        new AccessOsRules(["/Users/dev/work"], [], [new("/usr/bin/make", null)]),
        new AccessOsRules([@"C:\Work\Shop"], [], []), Shell: false, Cleared: false);

    private static bool Verifies(Guid workspace, Guid project, int version, string hash) =>
        AccessPolicySigning.Verify(Convert.FromBase64String(BrowserKey), workspace, project, version, hash, Convert.FromBase64String(BrowserSignature));

    [Fact]
    public void The_document_is_valid_and_canonical_and_encodes_to_the_bytes_and_hash_the_browser_computes()
    {
        Assert.Null(AccessPolicyRules.Validate(Document));
        Assert.True(AccessPolicyRules.IsCanonical(Document));
        Assert.Equal(EncodedLength, AccessPolicySigning.Encode(Document).Length);
        Assert.Equal(Hash, AccessPolicySigning.Hash(Document));
    }

    [Fact]
    public void The_payload_is_the_one_the_browser_signed_and_its_signature_verifies_only_for_that_workspace_project_version_and_hash()
    {
        Assert.Equal(Payload, Encoding.UTF8.GetString(AccessPolicySigning.Payload(Workspace, Project, 3, Hash)));
        Assert.True(Verifies(Workspace, Project, 3, Hash));
        Assert.False(Verifies(Workspace, Project, 4, Hash));
        Assert.False(Verifies(Workspace, Project, 2, Hash));
        Assert.False(Verifies(Project, Project, 3, Hash));
        Assert.False(Verifies(Workspace, Workspace, 3, Hash));
        Assert.False(Verifies(Workspace, Project, 3, new string('0', 64)));
    }
}
