using System.Security.Cryptography;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Tests;

/// <summary>UpdateModes and UpdateManifest (the agent's self-update contract): pure logic, no server.</summary>
public sealed class UpdateContractTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static (ECDsa Key, string Public) NewKey(ECCurve? curve = null)
    {
        var key = ECDsa.Create(curve ?? ECCurve.NamedCurves.nistP256);
        return (key, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    private static string Payload(string channel = "stable", string version = "0.3.1", string os = "macos", string arch = "arm64",
        string sha = Sha, string min = "0.2.0") => UpdateManifest.Payload(channel, version, os, arch, sha, min);

    // ---- UpdateModes ----

    [Theory]
    [InlineData("off", "off", "off")]
    [InlineData("off", "check", "off")]
    [InlineData("off", "on", "off")]
    [InlineData("check", "off", "off")]
    [InlineData("check", "check", "check")]
    [InlineData("check", "on", "check")]
    [InlineData("on", "off", "off")]
    [InlineData("on", "check", "check")]
    [InlineData("on", "on", "on")]
    [InlineData(null, "on", "off")]
    [InlineData("on", null, "off")]
    [InlineData(null, null, "off")]
    [InlineData("bogus", "on", "off")]
    [InlineData("on", "bogus", "off")]
    [InlineData("bogus", "bogus", "off")]
    [InlineData("ON", "on", "off")]
    [InlineData("", "check", "off")]
    public void Lower_is_the_stricter_of_two_modes_and_unknown_counts_as_off(string? a, string? b, string expected)
    {
        Assert.Equal(expected, UpdateModes.Lower(a, b));
        Assert.Equal(expected, UpdateModes.Lower(b, a));
    }

    [Theory]
    [InlineData("off", true)]
    [InlineData("check", true)]
    [InlineData("on", true)]
    [InlineData("always", false)]
    [InlineData("ON", false)]
    [InlineData("", false)]
    [InlineData(" off", false)]
    [InlineData(null, false)]
    public void IsValid_accepts_exactly_the_three_modes(string? mode, bool expected) =>
        Assert.Equal(expected, UpdateModes.IsValid(mode));

    [Theory]
    [InlineData("on", "on")]
    [InlineData("check", "check")]
    [InlineData("off", "off")]
    [InlineData("bogus", "off")]
    [InlineData(null, "off")]
    public void Normalize_maps_anything_unknown_to_off(string? mode, string expected) =>
        Assert.Equal(expected, UpdateModes.Normalize(mode));

    [Fact]
    public void All_lists_the_three_modes()
    {
        Assert.Equal(["off", "check", "on"], UpdateModes.All);
    }

    // ---- Payload ----

    [Fact]
    public void The_payload_is_the_exact_text_that_is_signed()
    {
        Assert.Equal(
            "cm-agent-update/1\nchannel=stable\nversion=0.3.1\nos=macos\narch=arm64\nsha256=" + Sha + "\nminSupported=0.2.0\n",
            Payload());
        Assert.Equal("cm-agent-update/1", UpdateManifest.Format);
    }

    [Theory]
    [InlineData("channel", "")]
    [InlineData("version", "")]
    [InlineData("os", "")]
    [InlineData("arch", "")]
    [InlineData("sha256", "")]
    [InlineData("minSupported", "")]
    [InlineData("channel", "a\nb")]
    [InlineData("version", "0.3.1\nos=windows")]
    [InlineData("os", "macos\r")]
    [InlineData("arch", "arm\r\n64")]
    [InlineData("sha256", "\n")]
    [InlineData("minSupported", "0.2.0\rx")]
    public void The_payload_refuses_an_empty_field_or_a_line_break(string field, string value)
    {
        string[] v = ["stable", "0.3.1", "macos", "arm64", Sha, "0.2.0"];
        v[Array.IndexOf(new[] { "channel", "version", "os", "arch", "sha256", "minSupported" }, field)] = value;
        Assert.Throws<ArgumentException>(() => UpdateManifest.Payload(v[0], v[1], v[2], v[3], v[4], v[5]));
    }

    [Fact]
    public void The_payload_refuses_a_null_field()
    {
        Assert.Throws<ArgumentException>(() => UpdateManifest.Payload(null!, "0.3.1", "macos", "arm64", Sha, "0.2.0"));
    }

    // ---- Sign / Verify ----

    [Fact]
    public void A_signature_verifies_under_its_own_key()
    {
        var (key, pub) = NewKey();
        using (key)
        {
            var payload = Payload();
            var signature = UpdateManifest.Sign(key, payload);
            Assert.True(UpdateManifest.Verify(pub, payload, signature));
        }
    }

    [Fact]
    public void Sign_needs_a_key()
    {
        Assert.Throws<ArgumentNullException>(() => UpdateManifest.Sign(null!, Payload()));
    }

    [Theory]
    [InlineData("channel")]
    [InlineData("version")]
    [InlineData("os")]
    [InlineData("arch")]
    [InlineData("sha256")]
    [InlineData("minSupported")]
    public void A_signature_does_not_cover_a_changed_field(string field)
    {
        var (key, pub) = NewKey();
        using (key)
        {
            var signature = UpdateManifest.Sign(key, Payload());
            var changed = field switch
            {
                "channel" => Payload(channel: "stablf"),
                "version" => Payload(version: "0.3.2"),
                "os" => Payload(os: "windows"),
                "arch" => Payload(arch: "x64"),
                "sha256" => Payload(sha: Sha[..^1] + "e"),
                "minSupported" => Payload(min: "0.2.1"),
                _ => throw new ArgumentOutOfRangeException(nameof(field)),
            };
            Assert.NotEqual(Payload(), changed);
            Assert.False(UpdateManifest.Verify(pub, changed, signature));
        }
    }

    [Fact]
    public void A_signature_of_another_key_is_refused()
    {
        var (signer, _) = NewKey();
        var (other, otherPublic) = NewKey();
        using (signer)
        using (other)
        {
            var payload = Payload();
            Assert.False(UpdateManifest.Verify(otherPublic, payload, UpdateManifest.Sign(signer, payload)));
        }
    }

    [Fact]
    public void A_p256_signature_under_a_p384_key_is_refused_and_so_is_an_rsa_key()
    {
        var (signer, _) = NewKey();
        var (p384, p384Public) = NewKey(ECCurve.NamedCurves.nistP384);
        using (signer)
        using (p384)
        using (var rsa = RSA.Create(2048))
        {
            var payload = Payload();
            var signature = UpdateManifest.Sign(signer, payload);
            Assert.False(UpdateManifest.Verify(p384Public, payload, signature));
            Assert.False(UpdateManifest.Verify(Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), payload, signature));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAAA")]
    [InlineData("not base64 at all!!")]
    [InlineData("%%%%")]
    [InlineData("MEUCIQ==")]
    public void A_garbage_signature_is_false_and_never_throws(string signature)
    {
        var (key, pub) = NewKey();
        using (key)
        {
            Assert.False(UpdateManifest.Verify(pub, Payload(), signature));
        }
    }

    [Fact]
    public void A_signature_that_is_a_valid_one_with_a_byte_flipped_or_cut_is_refused()
    {
        var (key, pub) = NewKey();
        using (key)
        {
            var payload = Payload();
            var bytes = Convert.FromBase64String(UpdateManifest.Sign(key, payload));
            Assert.True(UpdateManifest.Verify(pub, payload, Convert.ToBase64String(bytes)));
            var flipped = (byte[])bytes.Clone();
            flipped[^1] ^= 0x01;
            Assert.False(UpdateManifest.Verify(pub, payload, Convert.ToBase64String(flipped)));
            Assert.False(UpdateManifest.Verify(pub, payload, Convert.ToBase64String(bytes[..^2])));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAAA")]
    [InlineData("not base64 at all!!")]
    [InlineData("%%%%")]
    public void A_garbage_public_key_is_false_and_never_throws(string publicKey)
    {
        var (key, _) = NewKey();
        using (key)
        {
            Assert.False(UpdateManifest.Verify(publicKey, Payload(), UpdateManifest.Sign(key, Payload())));
        }
    }

    [Fact]
    public void Verify_with_null_inputs_never_throws()
    {
        Assert.False(UpdateManifest.Verify(null!, Payload(), "AAAA"));
        Assert.False(UpdateManifest.Verify("AAAA", Payload(), null!));
    }
}
