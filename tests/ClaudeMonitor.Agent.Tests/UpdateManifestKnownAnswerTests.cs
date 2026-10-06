using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>
/// A vector made by scripts/sign_manifest.py (OpenSSL) with a throw-away key whose private half was discarded: the .NET
/// verifier must accept what the release script signs, and nothing that differs by a single field.
/// </summary>
public sealed class UpdateManifestKnownAnswerTests
{
    private const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEPtM+Ht1ipSoyuRJ1RsyxPYimS5gy9Ca+F0I1Pf62BjFMLN9GYo3tBVlKeMrzK8N8HfKTJJpGTLITBB34NOlPVw==";
    private const string Signature = "MEYCIQD32fNyXMpM9WDuergFhD9kFYLbbKaW8PvmQyERB6R13QIhAP6oPcQja8twE0SiRI96pWj/7dBZSK0/EwTf6MbOYqNY";
    private static readonly string Sha = string.Concat(Enumerable.Repeat("ab", 32));

    private static string Payload(string channel = "test", string version = "0.3.1", string os = "macos", string arch = "arm64", string? sha = null,
        string min = "0.2.0") => UpdateManifest.Payload(channel, version, os, arch, sha ?? Sha, min);

    [Fact]
    public void The_python_signature_verifies_in_dotnet()
    {
        Assert.True(UpdateManifest.Verify(PublicKey, Payload(), Signature));
    }

    [Fact]
    public void The_payload_text_is_the_one_the_script_signs()
    {
        Assert.Equal($"cm-agent-update/1\nchannel=test\nversion=0.3.1\nos=macos\narch=arm64\nsha256={Sha}\nminSupported=0.2.0\n", Payload());
    }

    public static TheoryData<string, string> Variants => new()
    {
        { "channel", Payload(channel: "prod") },
        { "version", Payload(version: "0.3.2") },
        { "os", Payload(os: "windows") },
        { "arch", Payload(arch: "x64") },
        { "sha256", Payload(sha: string.Concat(Enumerable.Repeat("ab", 31)) + "ac") },
        { "minSupported", Payload(min: "0.2.1") },
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public void A_single_changed_field_does_not_verify(string field, string payload)
    {
        Assert.False(UpdateManifest.Verify(PublicKey, payload, Signature), field);
    }

    [Fact]
    public void A_damaged_signature_or_key_is_false_not_an_exception()
    {
        Assert.False(UpdateManifest.Verify(PublicKey, Payload(), Signature[..^6] + "AAAAAA"));
        Assert.False(UpdateManifest.Verify(PublicKey, Payload(), "not base64 !!"));
        Assert.False(UpdateManifest.Verify("not a key", Payload(), Signature));
        Assert.False(UpdateManifest.Verify("", Payload(), Signature));
    }
}
