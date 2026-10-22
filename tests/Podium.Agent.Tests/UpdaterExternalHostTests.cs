using System.Net;
using Podium.Agent.Update;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>A build offered on a download host (the API's AIPOD_DOWNLOADS_URL) is fetched from there and still has to match the signed hash.</summary>
public sealed class UpdaterExternalHostTests : IDisposable
{
    private const string Host = "downloads.example.net";
    private readonly UpdateKit kit = new();

    public UpdaterExternalHostTests() => kit.Host.ExternalHost = Host;

    public void Dispose() => kit.Dispose();

    private UpdateOffer OnHost(byte[] zip, string? version = null) =>
        kit.Offer(zip, version) with { Url = $"https://{Host}/daemons/prod/aipod-test.zip" };

    [Fact]
    public async Task A_verified_build_on_the_download_host_replaces_the_binary()
    {
        kit.InstallOld();
        var zip = kit.Zip();
        var offer = OnHost(zip);
        kit.Runner.VersionOutput = offer.Version + "\n";
        kit.Daemon.Version ??= offer.Version;
        kit.Serve(offer, zip);

        var result = await kit.Updater.ApplyAsync(offer, CancellationToken.None);

        Assert.Equal(UpdateCodes.Installed, result.Code);
        Assert.Equal(["/daemons/prod/aipod-test.zip"], kit.Host.Downloads);
        Assert.Equal("NEW-BINARY", File.ReadAllText(kit.Config.BinaryPath));
    }

    [Fact]
    public async Task A_build_on_the_download_host_whose_hash_is_not_the_signed_one_is_refused()
    {
        kit.InstallOld();
        var offer = OnHost(kit.Zip());
        kit.Serve(offer, kit.Zip([1, 2, 3]));
        kit.Runner.VersionOutput = offer.Version;

        await kit.ApplyRefusedAsync(offer, UpdateCodes.HashMismatch);
        Assert.Single(kit.Host.Downloads);
    }

    [Fact]
    public async Task A_missing_file_on_the_download_host_is_a_failed_download_not_an_install()
    {
        kit.InstallOld();
        var offer = OnHost(kit.Zip());
        kit.Runner.VersionOutput = offer.Version;

        await kit.ApplyRefusedAsync(offer, UpdateCodes.Failed);
    }

    [Fact]
    public async Task A_build_on_the_download_host_that_redirects_elsewhere_is_refused()
    {
        kit.InstallOld();
        var zip = kit.Zip();
        var offer = OnHost(zip);
        kit.Serve(offer, zip);
        kit.Host.RedirectedTo = new Uri("https://other.example.net/daemons/prod/aipod-test.zip");
        kit.Runner.VersionOutput = offer.Version;

        await kit.ApplyRefusedAsync(offer, UpdateCodes.BadUrl);
    }
}
