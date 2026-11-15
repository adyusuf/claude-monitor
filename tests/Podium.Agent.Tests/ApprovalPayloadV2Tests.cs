using System.Security.Cryptography;
using System.Text;
using Podium.Agent.Approvals;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>
/// Payload v2 on the target (ADR-0020 §4 rule 6): a run that names a repository is signed with it, so a server that relabels the run after the owner
/// signed, or drops its label, is refused here; a v1 signature never covers a run with a repository. The browser vector is shared with
/// web/src/lib/approvalKey.test.ts and RunPayloadV2Tests in the API.
/// </summary>
public sealed class ApprovalPayloadV2Tests
{
    private static readonly Guid Agent = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private const string Shop = "git:git.example.invalid/team/shop";
    private const string Other = "git:git.example.invalid/team/other";

    // The public half of a throwaway test key (its private half was discarded): a test vector, not a secret.
    private const string BrowserKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjAki8RDcqzMRG5/pFxCllxu5rY3N8smmesYh1PciGOvtLmcS1b7FxgGyhGzerDrnYBGknIb08ft4ivfiLNc0nQ=="; // gitleaks:allow
    private const string BrowserSignature = "SIRzFY7FcIeh60XUKL29arrtxxWU/xkXzP7kdgKkzX4wCNP7xEs0F+TaanAew7PCmEAvgw7DnsWhMBILBhwnSA==";
    private const string BrowserPayload = "ap-approval-v2\nrun\n0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e60\n0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e61\n"
        + "abababababababababababababababababababababababababababababababab\n1792000000\n" + Shop;

    private sealed class Owner : IDisposable
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string Id => ApprovalSigning.Fingerprint(key.ExportSubjectPublicKeyInfo());
        public ApproversPolicy Policy => new(true, [new Approver(Id, "laptop", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()))]);

        /// <summary>Signs the run the way the browser does (runPayload): v2 with the given repository, v1 when it is null.</summary>
        public RunMessage Sign(RunMessage run, string? signedKey)
        {
            var until = Now.AddMinutes(10);
            var hash = ApprovalVerifier.HashOf(run, Agent)!;
            var payload = signedKey is null
                ? ApprovalSigning.Payload(ApprovalSigning.KindRun, run.Id, Agent, hash, until)
                : ApprovalSigning.PayloadV2(ApprovalSigning.KindRun, run.Id, Agent, hash, until, signedKey);
            var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return run with { ApprovalKeyId = Id, ApprovalSignature = Convert.ToBase64String(signature), ApprovalNotAfter = until };
        }

        public void Dispose() => key.Dispose();
    }

    private static RunMessage Argv(string? projectKey) =>
        new(Guid.NewGuid(), RunModes.Argv, ["/usr/bin/tail", "-n", "5", "/var/log/app.log"], null, "/opt/ci", 60, Now.AddMinutes(20), null, null,
            ProjectKey: projectKey);

    [Fact]
    public void A_run_signed_with_its_repository_is_accepted()
    {
        using var owner = new Owner();

        Assert.Null(ApprovalVerifier.Refusal(owner.Policy, Agent, owner.Sign(Argv(Shop), Shop), Now));
        Assert.Null(ApprovalVerifier.Refusal(owner.Policy, Agent, owner.Sign(Argv(null), null), Now));
    }

    [Fact]
    public void A_server_that_relabels_or_unlabels_a_signed_run_is_refused()
    {
        using var owner = new Owner();
        var signed = owner.Sign(Argv(Shop), Shop);

        Assert.Equal(ApprovalErrors.Invalid, ApprovalVerifier.Refusal(owner.Policy, Agent, signed with { ProjectKey = Other }, Now));
        Assert.Equal(ApprovalErrors.Invalid, ApprovalVerifier.Refusal(owner.Policy, Agent, signed with { ProjectKey = null }, Now));
        Assert.Equal(ApprovalErrors.Invalid, ApprovalVerifier.Refusal(owner.Policy, Agent, signed with { ProjectKey = "" }, Now));
    }

    [Fact]
    public void A_v1_signature_never_covers_a_run_with_a_repository_and_a_v2_one_never_covers_a_run_without()
    {
        using var owner = new Owner();

        Assert.Equal(ApprovalErrors.Invalid, ApprovalVerifier.Refusal(owner.Policy, Agent, owner.Sign(Argv(Shop), null), Now));
        Assert.Equal(ApprovalErrors.Invalid, ApprovalVerifier.Refusal(owner.Policy, Agent, owner.Sign(Argv(null), Shop), Now));
    }

    [Fact]
    public void The_payload_this_agent_builds_for_a_labelled_run_is_the_one_the_browsers_webcrypto_signed()
    {
        var run = Argv(Shop) with { Id = Guid.Parse("0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e60"), ApprovalNotAfter = DateTimeOffset.FromUnixTimeSeconds(1792000000) };
        var hash = string.Concat(Enumerable.Repeat("ab", 32));

        var built = ApprovalVerifier.PayloadOf(run, Guid.Parse("0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e61"), hash);

        Assert.Equal(BrowserPayload, Encoding.UTF8.GetString(built));
        Assert.True(ApprovalSigning.Verify(Convert.FromBase64String(BrowserKey), built, Convert.FromBase64String(BrowserSignature)));
        var relabelled = ApprovalVerifier.PayloadOf(run with { ProjectKey = Other }, Guid.Parse("0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e61"), hash);
        Assert.False(ApprovalSigning.Verify(Convert.FromBase64String(BrowserKey), relabelled, Convert.FromBase64String(BrowserSignature)));
    }
}
