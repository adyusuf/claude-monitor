using System.Net;
using System.Security.Cryptography;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Updater.CheckAsync: a build is offered only when every check holds; each refusal has its own stable code and is recorded.</summary>
public sealed class UpdaterCheckTests : IDisposable
{
    private readonly UpdateKit kit = new();
    private readonly byte[] zip;

    public UpdaterCheckTests() => zip = kit.Zip();

    public void Dispose() => kit.Dispose();

    private async Task<UpdateCheck> Refused(UpdateOffer offer, string code)
    {
        kit.Publish(offer);
        var check = await kit.Updater.CheckAsync(CancellationToken.None);
        Assert.Equal(code, check.Code);
        Assert.True(check.Refused);
        Assert.Null(check.Offer); // a refused build is never handed on
        Assert.Equal(code, kit.State.Result);
        Assert.Null(kit.State.Available);
        Assert.NotNull(kit.State.CheckedAt);
        Assert.Contains($"update refused: {code}", await File.ReadAllTextAsync(kit.Config.LogPath), StringComparison.Ordinal);
        Assert.False(File.Exists(kit.Config.BinaryPath));
        Assert.False(Directory.Exists(kit.Config.UpdateDir));
        Assert.Empty(kit.Host.Downloads);
        return check;
    }

    [Fact]
    public async Task An_operating_system_the_agent_is_not_built_for_asks_nothing_and_updates_nothing()
    {
        using var kit = new UpdateKit(c => c with { UpdateOs = "unsupported" });
        var check = await kit.Updater.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateCodes.Unsupported, check.Code);
        Assert.Equal(0, kit.Api.Count("GET /api/agent/latest*"));
    }

    [Fact]
    public async Task A_signature_made_with_another_key_is_refused()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var good = kit.Offer(zip);
        var forged = good with { Signature = kit.Sign(UpdateKit.Channel, good.Version, good.Sha256, good.MinSupported, other) };
        await Refused(forged, UpdateCodes.BadSignature);
    }

    [Fact]
    public async Task A_hash_changed_after_signing_is_refused()
    {
        var good = kit.Offer(zip);
        await Refused(good with { Sha256 = UpdateKit.Sha256([1, 2, 3]) }, UpdateCodes.BadSignature);
    }

    [Fact]
    public async Task A_version_changed_after_signing_is_refused()
    {
        var good = kit.Offer(zip);
        await Refused(good with { Version = UpdateKit.Newest }, UpdateCodes.BadSignature);
    }

    [Fact]
    public async Task A_minimum_supported_changed_after_signing_is_refused()
    {
        var good = kit.Offer(zip, minSupported: "0.0.1");
        await Refused(good with { MinSupported = UpdateKit.Newer }, UpdateCodes.BadSignature);
    }

    [Fact]
    public async Task A_signature_that_is_not_base64_is_refused_not_thrown()
    {
        var good = kit.Offer(zip);
        await Refused(good with { Signature = "!!not base64!!" }, UpdateCodes.BadSignature);
    }

    [Fact]
    public async Task An_offer_for_another_channel_is_refused_even_when_correctly_signed()
    {
        var offer = kit.Offer(zip, channel: "prod"); // signed with the right key, for "prod"; this agent is "test"
        var check = await Refused(offer, UpdateCodes.Channel);
        Assert.Contains("prod", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_build_without_a_key_refuses_everything_and_does_not_even_ask_the_server()
    {
        using var keyless = new UpdateKit(c => c with { UpdatePublicKey = "" });
        keyless.Publish(keyless.Offer(keyless.Zip()));
        var check = await keyless.Updater.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateCodes.NoKey, check.Code);
        Assert.True(check.Refused);
        Assert.Empty(keyless.Api.Seen);
        Assert.Equal(0, keyless.Api.Count("GET /api/agent/latest"));
        Assert.Equal(UpdateCodes.NoKey, keyless.State.Result);
    }

    [Fact]
    public async Task The_same_version_is_up_to_date_and_not_a_refusal()
    {
        kit.Publish(kit.Offer(zip, AgentConfig.Version));
        var check = await kit.Updater.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateCodes.UpToDate, check.Code);
        Assert.False(check.Refused);
        Assert.Null(check.Offer);
        Assert.Equal(UpdateCodes.UpToDate, kit.State.Result);
        Assert.Null(kit.State.Available);
    }

    [Fact]
    public async Task An_older_signed_build_is_a_downgrade_and_never_offered()
    {
        await Refused(kit.Offer(zip, UpdateKit.Older), UpdateCodes.Downgrade);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("zz00000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("00000000000000000000000000000000000000000000000000000000000000000")]
    public async Task A_hash_that_is_not_64_hex_characters_is_malformed(string sha)
    {
        await Refused(kit.Offer(zip) with { Sha256 = sha }, UpdateCodes.Malformed);
    }

    [Theory]
    [InlineData("x", "0.0.1")]
    [InlineData("1.2", "0.0.1")]
    [InlineData("1.2.3-beta", "0.0.1")]
    [InlineData("9.9.9", "banana")]
    public async Task A_version_or_minimum_that_is_not_a_plain_x_y_z_is_malformed(string version, string minimum)
    {
        await Refused(kit.Offer(zip) with { Version = version, MinSupported = minimum }, UpdateCodes.Malformed);
    }

    [Fact]
    public async Task An_offer_without_a_signature_is_malformed()
    {
        await Refused(kit.Offer(zip) with { Signature = "" }, UpdateCodes.Malformed);
    }

    [Fact]
    public async Task No_published_build_is_up_to_date_with_the_reason_in_the_message()
    {
        var check = await kit.Updater.CheckAsync(CancellationToken.None); // the fake server answers 404
        Assert.Equal(UpdateCodes.UpToDate, check.Code);
        Assert.Contains("no update is published", check.Message, StringComparison.Ordinal);
        Assert.Equal(1, kit.Api.Count("GET /api/agent/latest"));
        Assert.Equal(UpdateCodes.UpToDate, kit.State.Result);
    }

    [Fact]
    public async Task The_question_names_this_machines_os_and_cpu()
    {
        await kit.Updater.CheckAsync(CancellationToken.None);
        Assert.Equal($"?os={kit.Config.UpdateOs}&arch={kit.Config.UpdateArch}", Assert.Single(kit.Host.Queries));
    }

    [Fact]
    public async Task An_unreachable_server_is_a_verdict_not_an_exception()
    {
        kit.Api.On("GET /api/agent/latest*", _ => throw new HttpRequestException("no route to host"));
        var check = await kit.Updater.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateCodes.Unreachable, check.Code);
        Assert.True(check.Refused);
        Assert.Equal(UpdateCodes.Unreachable, kit.State.Result);
        Assert.Contains("HttpRequestException", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_error_is_unreachable_too()
    {
        kit.Api.On("GET /api/agent/latest*", HttpStatusCode.BadGateway, "{}");
        Assert.Equal(UpdateCodes.Unreachable, (await kit.Updater.CheckAsync(CancellationToken.None)).Code);
    }

    [Fact]
    public async Task A_valid_newer_offer_is_available_and_remembered()
    {
        var offer = kit.Publish(kit.Offer(zip));
        var check = await kit.Updater.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateCodes.Available, check.Code);
        Assert.False(check.Refused);
        Assert.Equal(offer, check.Offer);
        Assert.False(check.BelowMinimum);
        Assert.Equal((UpdateCodes.Available, offer.Version), (kit.State.Result, kit.State.Available));
        Assert.NotNull(kit.State.CheckedAt);
        Assert.False(File.Exists(kit.Config.BinaryPath)); // a check changes nothing but the state file
        Assert.Empty(kit.Host.Downloads);
    }

    [Fact]
    public async Task An_upper_case_hash_is_signed_and_checked_in_lower_case()
    {
        var good = kit.Offer(zip);
        kit.Publish(good with { Sha256 = good.Sha256.ToUpperInvariant() });
        Assert.Equal(UpdateCodes.Available, (await kit.Updater.CheckAsync(CancellationToken.None)).Code);
    }

    [Fact]
    public async Task A_minimum_above_the_running_version_is_flagged_for_the_person()
    {
        kit.Publish(kit.Offer(zip, minSupported: UpdateKit.Newer));
        var check = await kit.Updater.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateCodes.Available, check.Code);
        Assert.True(check.BelowMinimum);
    }

    [Fact]
    public async Task A_later_check_replaces_the_earlier_verdict()
    {
        kit.Publish(kit.Offer(zip));
        await kit.Updater.CheckAsync(CancellationToken.None);
        Assert.NotNull(kit.State.Available);
        kit.Api.Routes.Clear(); // the server no longer publishes anything
        await kit.Updater.CheckAsync(CancellationToken.None);
        Assert.Null(kit.State.Available);
        Assert.Equal(UpdateCodes.UpToDate, kit.State.Result);
    }
}
