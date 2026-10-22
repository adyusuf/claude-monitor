using System.Security.Cryptography;
using System.Text;
using Podium.Agent.Approvals;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The target's own check of a signed approval (ADR-0010 §4): what verifies, what each tampering breaks, and the policy file that pins the keys.</summary>
public sealed class ApprovalVerifierTests
{
    private static readonly Guid Agent = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class Owner
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string Spki => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        public string Id => ApprovalSigning.Fingerprint(key.ExportSubjectPublicKeyInfo());
        public ApproversPolicy Policy(bool required = true) => new(required, [new Approver(Id, "laptop", Spki)]);

        public RunMessage Sign(RunMessage run, Guid? agent = null, DateTimeOffset? notAfter = null)
        {
            var until = notAfter ?? Now.AddMinutes(10);
            var hash = ApprovalVerifier.HashOf(run, agent ?? Agent)!;
            var signature = key.SignData(ApprovalSigning.Payload(ApprovalSigning.KindRun, run.Id, agent ?? Agent, hash, until), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return run with { ApprovalKeyId = Id, ApprovalSignature = Convert.ToBase64String(signature), ApprovalNotAfter = until };
        }
    }

    private static RunMessage Argv(string[]? argv = null, string? cwd = "/opt/ci", int timeout = 60) =>
        new(Guid.NewGuid(), RunModes.Argv, argv ?? ["/usr/bin/tail", "-n", "5", "/var/log/app.log"], null, cwd, timeout, Now.AddMinutes(20), null, null);

    private static RunMessage Shell(string command = "ls -l") => new(Guid.NewGuid(), RunModes.Shell, null, command, null, 60, Now.AddMinutes(20), null, null);

    private static RunMessage Script(IReadOnlyList<ScriptFile>? files = null, string? stdin = null, string? env = "test", string? function = "application") =>
        new(Guid.NewGuid(), RunModes.Script, null, null, null, 60, Now.AddMinutes(20), null, null, null, "sh", "echo hi\n", ["a"], 1 << 20, false, files, stdin, null, null, null, env, function);

    [Fact]
    public void A_run_the_pinned_owner_signed_is_accepted_in_every_mode()
    {
        var owner = new Owner();
        foreach (var run in new[] { Argv(), Shell(), Script(), Script([new("data.txt", "x")], "in") })
        {
            Assert.Null(ApprovalVerifier.Refusal(owner.Policy(), Agent, owner.Sign(run), Now));
        }
    }

    [Fact]
    public void Without_the_mode_on_nothing_is_checked_and_an_unsigned_run_passes_as_it_always_did()
    {
        Assert.Null(ApprovalVerifier.Refusal(ApproversPolicy.Off, Agent, Argv(), Now));
        Assert.Null(ApprovalVerifier.Refusal(new Owner().Policy(required: false), Agent, Argv(), Now));
    }

    [Fact]
    public void Every_field_the_owner_signed_breaks_the_signature_when_the_api_changes_it()
    {
        var owner = new Owner();
        var signedArgv = owner.Sign(Argv());
        var signedShell = owner.Sign(Shell());
        var signedScript = owner.Sign(Script([new("data.txt", "x")], "in"));
        var cases = new (string Name, RunMessage Run)[]
        {
            ("argv", signedArgv with { Argv = ["/usr/bin/tail", "-n", "5", "/etc/shadow"] }),
            ("cwd", signedArgv with { Cwd = "/" }),
            ("timeout", signedArgv with { TimeoutSeconds = 61 }),
            ("mode", signedArgv with { Mode = RunModes.Shell, ShellCommand = "ls" }),
            ("shell", signedShell with { ShellCommand = "ls -l; id" }),
            ("body", signedScript with { ScriptBody = "echo no\n" }),
            ("interpreter", signedScript with { ScriptInterpreter = "bash" }),
            ("args", signedScript with { ScriptArgs = ["b"] }),
            ("file text", signedScript with { ScriptFiles = [new("data.txt", "y")] }),
            ("file path", signedScript with { ScriptFiles = [new("other.txt", "x")] }),
            ("an extra file", signedScript with { ScriptFiles = [new("data.txt", "x"), new("z.txt", "1")] }),
            ("stdin", signedScript with { ScriptStdin = "other" }),
            ("environment", signedScript with { TargetEnvironment = "production" }),
            ("function", signedScript with { TargetFunction = "database" }),
            ("run id", signedArgv with { Id = Guid.NewGuid() }),
            ("signed expiry", signedArgv with { ApprovalNotAfter = signedArgv.ApprovalNotAfter!.Value.AddMinutes(1) }),
        };
        foreach (var (name, run) in cases) Assert.True(ApprovalVerifier.Refusal(owner.Policy(), Agent, run, Now) == ApprovalErrors.Invalid, name);
    }

    [Fact]
    public void A_signature_made_for_another_agent_or_by_another_key_or_flipped_is_refused()
    {
        var owner = new Owner();
        var other = new Owner();
        Assert.Equal(ApprovalErrors.Invalid, ApprovalVerifier.Refusal(owner.Policy(), Agent, owner.Sign(Argv(), agent: Guid.NewGuid()), Now));
        Assert.Equal(ApprovalErrors.KeyUnknown, ApprovalVerifier.Refusal(owner.Policy(), Agent, other.Sign(Argv()), Now));
        var run = owner.Sign(Argv());
        var bytes = Convert.FromBase64String(run.ApprovalSignature!);
        bytes[10] ^= 1;
        Assert.Equal(ApprovalErrors.Invalid, ApprovalVerifier.Refusal(owner.Policy(), Agent, run with { ApprovalSignature = Convert.ToBase64String(bytes) }, Now));
        Assert.Equal(ApprovalErrors.Invalid, ApprovalVerifier.Refusal(owner.Policy(), Agent, run with { ApprovalSignature = "not base64 !!" }, Now));
        Assert.Equal(ApprovalErrors.Invalid, ApprovalVerifier.Refusal(owner.Policy(), Agent, run with { ApprovalSignature = Convert.ToBase64String(new byte[10]) }, Now));
    }

    [Fact]
    public void A_missing_signature_a_grant_run_no_pinned_key_and_an_unknown_identity_are_refused_when_signatures_are_required()
    {
        var owner = new Owner();
        Assert.Equal(ApprovalErrors.Required, ApprovalVerifier.Refusal(owner.Policy(), Agent, Argv(), Now));
        var signed = owner.Sign(Argv());
        Assert.Equal(ApprovalErrors.Required, ApprovalVerifier.Refusal(owner.Policy(), Agent, signed with { GrantId = Guid.NewGuid(), Grant = new GrantTemplate(["/usr/bin/tail"], "/opt", 60) }, Now));
        Assert.Equal(ApprovalErrors.Required, ApprovalVerifier.Refusal(owner.Policy(), Agent, signed with { ApprovalKeyId = null }, Now));
        Assert.Equal(ApprovalErrors.Required, ApprovalVerifier.Refusal(owner.Policy(), Agent, signed with { ApprovalNotAfter = null }, Now));
        Assert.Equal(ApprovalErrors.KeyMissing, ApprovalVerifier.Refusal(new ApproversPolicy(true, []), Agent, signed, Now));
        Assert.Equal(ApprovalErrors.KeyMissing, ApprovalVerifier.Refusal(owner.Policy(), null, signed, Now)); // an agent that does not know its own id runs nothing
    }

    [Fact]
    public void The_signed_expiry_holds_for_a_grace_after_it_and_not_beyond()
    {
        var owner = new Owner();
        var run = owner.Sign(Argv(), notAfter: Now);
        Assert.Null(ApprovalVerifier.Refusal(owner.Policy(), Agent, run, Now + ApprovalVerifier.Grace));
        Assert.Equal(ApprovalErrors.Invalid, ApprovalVerifier.Refusal(owner.Policy(), Agent, run, Now + ApprovalVerifier.Grace + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void The_key_id_is_matched_without_regard_to_letter_case()
    {
        var owner = new Owner();
        var run = owner.Sign(Argv());
        Assert.Null(ApprovalVerifier.Refusal(owner.Policy(), Agent, run with { ApprovalKeyId = run.ApprovalKeyId!.ToUpperInvariant() }, Now));
    }

    // ---- a signature made by a real browser (Node WebCrypto), checked by this code --------------------------------------------

    // The public half of a throwaway test key (its private half was discarded): a test vector, not a secret.
    private const string BrowserKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjVzUVruijJwX3hreZFoDwzCsbjf0RYXr+k4vTRcDjPUj5UHd5D5KpBV7fnHqAulEYmymSVtNi037+o52d+s1ng=="; // gitleaks:allow
    private const string BrowserSignature = "QL8DauuF1hk1MwECldHBnuZb8vFDs5fNwLU7f8bd492Je//c0eQPfdEflZ5SAGSX4o2hfV9C2plJnFU5291/dA==";
    private const string BrowserPayload = "ap-approval-v1\nrun\n0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e60\n0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e61\nabababababababababababababababababababababababababababababababab\n1792000000";

    [Fact]
    public void A_signature_the_browsers_webcrypto_made_verifies_here_and_the_payload_this_code_builds_is_the_one_it_signed()
    {
        var spki = Convert.FromBase64String(BrowserKey);
        Assert.True(ApprovalSigning.IsValidPublicKey(spki));
        var built = ApprovalSigning.Payload(ApprovalSigning.KindRun, Guid.Parse("0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e60"), Guid.Parse("0198f1c2-7a55-7d3a-9b1e-2f6c3a4d5e61"),
            new string('a', 1) + "b" + string.Concat(Enumerable.Repeat("ab", 31)), DateTimeOffset.FromUnixTimeSeconds(1792000000));
        Assert.Equal(BrowserPayload, Encoding.UTF8.GetString(built));
        Assert.True(ApprovalSigning.Verify(spki, built, Convert.FromBase64String(BrowserSignature)));
        var changed = Encoding.UTF8.GetBytes(BrowserPayload.Replace("1792000000", "1792000001", StringComparison.Ordinal));
        Assert.False(ApprovalSigning.Verify(spki, changed, Convert.FromBase64String(BrowserSignature)));
    }

    [Fact]
    public void Only_a_p256_public_key_is_a_key_and_a_fingerprint_is_the_sha256_of_its_bytes()
    {
        Assert.False(ApprovalSigning.IsValidPublicKey([1, 2, 3]));
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.False(ApprovalSigning.IsValidPublicKey(p384.ExportSubjectPublicKeyInfo()));
        using var rsa = RSA.Create(2048);
        Assert.False(ApprovalSigning.IsValidPublicKey(rsa.ExportSubjectPublicKeyInfo()));
        var spki = Convert.FromBase64String(BrowserKey);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(spki)), ApprovalSigning.Fingerprint(spki));
        Assert.Null(ApprovalSigning.FromBase64(""));
        Assert.Null(ApprovalSigning.FromBase64("%%%"));
        Assert.Equal(spki, ApprovalSigning.FromBase64(BrowserKey.Replace('+', '-').Replace('/', '_').TrimEnd('='))); // url-safe without padding is understood too
    }
}

public sealed class ApproversStoreTests : IDisposable
{
    private readonly TempHome home = new();

    public void Dispose() => home.Dispose();

    private static string NewKey() => Convert.ToBase64String(ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfo());

    [Fact]
    public void Without_a_file_signatures_are_not_required_and_nothing_is_pinned()
    {
        var policy = ApproversStore.Load(home.Config);
        Assert.False(policy.Required);
        Assert.Empty(policy.Keys);
    }

    [Fact]
    public void A_key_is_pinned_by_its_fingerprint_listed_and_removed_and_the_mode_is_a_separate_switch()
    {
        var key = NewKey();
        var id = ApproversStore.Add(home.Config, key, "laptop\nwith a line break");
        Assert.Equal(ApprovalSigning.Fingerprint(Convert.FromBase64String(key)), id);
        var policy = ApproversStore.Load(home.Config);
        Assert.False(policy.Required);
        var pinned = Assert.Single(policy.Keys);
        Assert.Equal((id, "laptopwith a line break"), (pinned.Id, pinned.Name));

        ApproversStore.SetRequired(home.Config, true);
        Assert.True(ApproversStore.Load(home.Config).Required);
        Assert.Equal(id, ApproversStore.Add(home.Config, key, "again")); // pinning the same key twice keeps one entry
        Assert.Single(ApproversStore.Load(home.Config).Keys);

        Assert.True(ApproversStore.Remove(home.Config, id.ToUpperInvariant()));
        Assert.False(ApproversStore.Remove(home.Config, id));
        Assert.Empty(ApproversStore.Load(home.Config).Keys);
        Assert.True(ApproversStore.Load(home.Config).Required); // removing the last key does not switch the mode off: nothing runs, which is the safe state
    }

    [Fact]
    public void A_key_that_is_not_a_p256_public_key_is_refused_and_nothing_is_written()
    {
        Assert.Throws<ArgumentException>(() => ApproversStore.Add(home.Config, "bm90IGEga2V5", "x"));
        Assert.Throws<ArgumentException>(() => ApproversStore.Add(home.Config, Convert.ToBase64String(RSA.Create(2048).ExportSubjectPublicKeyInfo()), "x"));
        Assert.False(File.Exists(home.Config.ApproversPath));
    }

    [Fact]
    public void A_file_that_cannot_be_trusted_means_required_with_no_key_so_nothing_runs()
    {
        Directory.CreateDirectory(home.Dir);
        foreach (var text in new[] { "{ not json", "null", "[]" })
        {
            File.WriteAllText(home.Config.ApproversPath, text);
            var policy = ApproversStore.Load(home.Config);
            Assert.True(policy.Required, text);
            Assert.Empty(policy.Keys);
        }
    }

    [Fact]
    public void An_entry_whose_id_is_not_the_fingerprint_of_its_key_is_ignored_so_a_file_edit_cannot_rename_a_key()
    {
        var key = NewKey();
        var id = ApproversStore.Add(home.Config, key, "ok");
        var text = File.ReadAllText(home.Config.ApproversPath).Replace(id, new string('0', 64), StringComparison.Ordinal);
        File.WriteAllText(home.Config.ApproversPath, text);
        Assert.Empty(ApproversStore.Load(home.Config).Keys);
    }

    [Fact]
    public void A_service_agent_trusts_the_file_only_when_an_admin_owns_it()
    {
        var service = home.Config with { ServiceMode = true, ExecConfigPath = Path.Combine(home.Dir, "etc", "exec.json") };
        Directory.CreateDirectory(Path.GetDirectoryName(service.ExecConfigPath)!);
        Assert.False(ApproversStore.Load(service).Required); // no file: not required
        Assert.Equal(Path.Combine(home.Dir, "etc", "approvers.json"), service.ApproversPath);
        ApproversStore.Add(service, NewKey(), "x");
        var policy = ApproversStore.Load(service); // a temp folder is owned by the test's user, not by root
        Assert.True(policy.Required);
        Assert.Empty(policy.Keys);
    }

    [Fact]
    public void A_link_is_never_followed()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(home.Dir);
        var real = Path.Combine(home.Dir, "real.json");
        File.WriteAllText(real, """{"required":false,"keys":[]}""");
        File.CreateSymbolicLink(home.Config.ApproversPath, real);
        Assert.True(ApproversStore.Load(home.Config).Required);
    }

    [Fact]
    public void The_status_line_says_the_mode_and_how_many_keys_and_never_a_key()
    {
        var id = ApproversStore.Add(home.Config, NewKey(), "x");
        var line = ApproversStore.Describe(ApproversStore.Load(home.Config));
        Assert.Equal("signed approvals: off, 1 key(s) pinned", line);
        Assert.DoesNotContain(id, line, StringComparison.Ordinal);
    }

    // ---- the command -------------------------------------------------------------------------------------------------------

    private async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await ApproversCommand.RunAsync(["approvers", .. args], home.Config, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task The_command_pins_lists_and_removes_and_says_to_compare_the_fingerprint()
    {
        var key = NewKey();
        var id = ApprovalSigning.Fingerprint(Convert.FromBase64String(key));
        var added = await RunAsync("add", key, "--name", "laptop");
        Assert.Equal(0, added.Code);
        Assert.Contains($"pinned {id}", added.Out, StringComparison.Ordinal);
        Assert.Contains("Compare this fingerprint", added.Out, StringComparison.Ordinal);
        Assert.Contains("not required yet", added.Out, StringComparison.Ordinal);

        var listed = await RunAsync("list");
        Assert.Contains($"{id}  laptop", listed.Out, StringComparison.Ordinal);
        Assert.Equal(1, (await RunAsync("remove", new string('f', 64))).Code);
        var removed = await RunAsync("remove", id);
        Assert.Equal((0, "removed\n"), (removed.Code, removed.Out.Replace("\r", "", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_command_refuses_a_bad_key_and_a_wrong_shape_of_arguments()
    {
        var bad = await RunAsync("add", "bm90IGEga2V5");
        Assert.Equal(2, bad.Code);
        Assert.Contains("not an ECDSA P-256 public key", bad.Err, StringComparison.Ordinal);
        Assert.Equal(2, (await RunAsync()).Code);
        Assert.Equal(2, (await RunAsync("add")).Code);
        Assert.Contains("usage:", (await RunAsync("frobnicate")).Err, StringComparison.Ordinal);
    }
}
