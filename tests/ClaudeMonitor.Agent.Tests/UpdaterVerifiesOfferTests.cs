using System.Security.Cryptography;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>
/// ApplyAsync judges the offer itself: whoever hands it one (the CLI, the daemon's spawned updater, a future caller) never
/// needs CheckAsync to have looked first. Nothing is asked of the server and nothing on disk changes for a refused offer.
/// </summary>
public sealed class UpdaterVerifiesOfferTests : IDisposable
{
    private UpdateKit kit = new();

    public void Dispose() => kit.Dispose();

    /// <summary>The offer's zip is served and would pass every later check, so a missing early check would let it install.</summary>
    private async Task NeverChecked(UpdateOffer offer, byte[] zip, string code)
    {
        kit.InstallOld();
        kit.Serve(offer, zip);
        kit.Runner.VersionOutput = offer.Version;
        await kit.ApplyRefusedAsync(offer, code);
        Assert.Empty(kit.Api.Seen);
        Assert.Equal(0, kit.Api.Count("GET /api/agent/latest"));
        Assert.Empty(kit.Host.Downloads);
        Assert.Empty(kit.Runner.Calls);
    }

    [Fact]
    public async Task A_signature_of_another_key_is_refused_without_a_call()
    {
        var zip = kit.Zip();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var good = kit.Offer(zip);
        await NeverChecked(good with { Signature = kit.Sign(UpdateKit.Channel, good.Version, good.Sha256, good.MinSupported, other) }, zip, UpdateCodes.BadSignature);
    }

    [Fact]
    public async Task A_hash_changed_after_signing_is_refused_without_a_call()
    {
        var zip = kit.Zip();
        await NeverChecked(kit.Offer(zip) with { Sha256 = UpdateKit.Sha256([1, 2, 3]) }, zip, UpdateCodes.BadSignature);
    }

    [Fact]
    public async Task An_offer_for_another_channel_is_refused_without_a_call_even_when_correctly_signed()
    {
        var zip = kit.Zip();
        await NeverChecked(kit.Offer(zip, channel: "prod"), zip, UpdateCodes.Channel);
    }

    [Fact]
    public async Task The_running_version_is_up_to_date_and_nothing_is_installed()
    {
        var zip = kit.Zip();
        await NeverChecked(kit.Offer(zip, AgentConfig.Version), zip, UpdateCodes.UpToDate);
    }

    [Fact]
    public async Task An_older_signed_version_is_a_downgrade_and_is_refused_without_a_call()
    {
        var zip = kit.Zip();
        await NeverChecked(kit.Offer(zip, UpdateKit.Older), zip, UpdateCodes.Downgrade);
    }

    [Theory]
    [InlineData("1.2", "0.0.1", "", "")]
    [InlineData("9.9.9", "banana", "", "")]
    [InlineData("9.9.9", "0.0.1", "short", "")]
    [InlineData("9.9.9", "0.0.1", "", "empty-signature")]
    public async Task A_malformed_offer_is_refused_without_a_call(string version, string minSupported, string sha, string signature)
    {
        var zip = kit.Zip();
        var offer = kit.Offer(zip);
        offer = offer with
        {
            Version = version,
            MinSupported = minSupported,
            Sha256 = sha.Length == 0 ? offer.Sha256 : sha,
            Signature = signature == "empty-signature" ? "" : offer.Signature,
        };
        await NeverChecked(offer, zip, UpdateCodes.Malformed);
    }

    [Fact]
    public async Task A_build_without_a_key_installs_nothing_even_when_the_offer_is_correctly_signed()
    {
        kit.Dispose();
        kit = new UpdateKit(c => c with { UpdatePublicKey = "" });
        var zip = kit.Zip();
        await NeverChecked(kit.Offer(zip), zip, UpdateCodes.NoKey);
    }

    [Fact]
    public async Task A_correctly_signed_newer_offer_that_was_never_checked_still_installs()
    {
        kit.InstallOld();
        var offer = kit.PublishGood();

        var result = await kit.Updater.ApplyAsync(offer, CancellationToken.None); // no CheckAsync before it

        Assert.Equal(UpdateCodes.Installed, result.Code);
        Assert.Equal("NEW-BINARY", File.ReadAllText(kit.Config.BinaryPath));
        Assert.Equal(0, kit.Api.Count("GET /api/agent/latest")); // it did not even ask the server
    }
}
