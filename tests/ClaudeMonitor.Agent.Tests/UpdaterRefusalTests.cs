using System.IO.Compression;
using System.Net;
using System.Text;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Updater.ApplyAsync refusals: download, archive and binary checks; every one leaves the installation exactly as it was.</summary>
public sealed class UpdaterRefusalTests : IDisposable
{
    private UpdateKit kit = new();

    public void Dispose() => kit.Dispose();

    private Task<UpdateResult> Refused(UpdateOffer offer, string code) => kit.ApplyRefusedAsync(offer, code);

    // ---- refusals that leave the disk untouched ---------------------------------------------------------------

    [Fact]
    public async Task A_download_whose_hash_is_not_the_signed_one_is_refused()
    {
        kit.InstallOld();
        var offer = kit.Offer(kit.Zip(Encoding.ASCII.GetBytes("WHAT WAS SIGNED")));
        kit.Serve(offer, kit.Zip(Encoding.ASCII.GetBytes("SOMETHING ELSE")));
        kit.Runner.VersionOutput = offer.Version;
        await Refused(offer, UpdateCodes.HashMismatch);
        Assert.NotNull(kit.State.AttemptedAt);
        Assert.Single(kit.Host.Downloads); // it was downloaded, then refused
    }

    [Theory]
    [InlineData("https://evil.invalid/downloads/a.zip")]
    [InlineData("https://monitor.invalid:8443/downloads/a.zip")]
    [InlineData("http://monitor.invalid/downloads/a.zip")]
    [InlineData("https://monitor.invalid/other/a.zip")]
    [InlineData("https://monitor.invalid/downloadsx/a.zip")]
    [InlineData("https://monitor.invalid/")]
    [InlineData("/downloads/a.zip")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task A_download_address_off_this_servers_downloads_is_refused_before_anything_is_fetched(string url)
    {
        kit.InstallOld();
        var zip = kit.Zip();
        var offer = kit.Offer(zip) with { Url = url };
        kit.Runner.VersionOutput = offer.Version;
        await Refused(offer, UpdateCodes.BadUrl);
        Assert.Empty(kit.Host.Downloads);
        Assert.Empty(kit.Runner.Calls);
    }

    [Fact]
    public async Task A_redirected_download_is_refused()
    {
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Host.RedirectedTo = new Uri("https://evil.invalid/downloads/elsewhere.zip");
        await Refused(offer, UpdateCodes.BadUrl);
        Assert.Empty(kit.Runner.Calls);
    }

    private static byte[] EmptyZip()
    {
        using var buffer = new MemoryStream();
        using (new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task An_archive_with_two_files_is_refused()
    {
        kit.InstallOld();
        var zip = kit.Zip(null, kit.BinaryName, "extra.txt");
        var offer = kit.Offer(zip);
        kit.Serve(offer, zip);
        kit.Runner.VersionOutput = offer.Version;
        await Refused(offer, UpdateCodes.BadArchive);
        Assert.Empty(kit.Runner.Calls);
    }

    [Theory]
    [InlineData("other-name")]
    [InlineData("../cm-agent")]
    [InlineData("../../cm-agent")]
    [InlineData("sub/cm-agent")]
    [InlineData("/cm-agent")]
    public async Task An_archive_whose_file_is_not_named_like_the_binary_is_refused(string entry)
    {
        kit.InstallOld();
        var zip = kit.Zip(null, entry.Replace("cm-agent", kit.BinaryName, StringComparison.Ordinal));
        var offer = kit.Offer(zip);
        kit.Serve(offer, zip);
        kit.Runner.VersionOutput = offer.Version;
        await Refused(offer, UpdateCodes.BadArchive);
        Assert.False(File.Exists(Path.Combine(kit.Dir, "cm-agent")), "nothing was written outside the home");
    }

    [Fact]
    public async Task An_empty_archive_is_refused()
    {
        kit.InstallOld();
        var zip = EmptyZip();
        var offer = kit.Offer(zip);
        kit.Serve(offer, zip);
        await Refused(offer, UpdateCodes.BadArchive);
    }

    [Fact]
    public async Task A_download_larger_than_allowed_is_refused_by_its_announced_size()
    {
        kit.Dispose();
        kit = new UpdateKit(c => c with { UpdateDownloadMax = 10 });
        kit.InstallOld();
        var offer = kit.PublishGood();
        await Refused(offer, UpdateCodes.TooLarge);
        Assert.Empty(kit.Runner.Calls);
    }

    [Fact]
    public async Task A_download_that_hides_its_size_is_refused_by_counting_the_bytes()
    {
        kit.Dispose();
        kit = new UpdateKit(c => c with { UpdateDownloadMax = 10 });
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Host.UnknownLength = true;
        await Refused(offer, UpdateCodes.TooLarge);
    }

    [Fact]
    public async Task A_binary_larger_than_allowed_inside_the_archive_is_refused()
    {
        kit.Dispose();
        kit = new UpdateKit(c => c with { UpdateBinaryMax = 50 });
        kit.InstallOld();
        var zip = kit.Zip(new byte[500]);
        var offer = kit.Offer(zip);
        kit.Serve(offer, zip);
        kit.Runner.VersionOutput = offer.Version;
        Assert.True(zip.Length < kit.Config.UpdateDownloadMax);
        await Refused(offer, UpdateCodes.TooLarge);
        Assert.Empty(kit.Runner.Calls);
    }

    [Fact]
    public async Task A_binary_that_reports_another_version_is_refused()
    {
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Runner.VersionOutput = UpdateKit.Newest;
        await Refused(offer, UpdateCodes.BadBinary);
    }

    [Fact]
    public async Task A_binary_that_does_not_run_is_refused_even_if_it_printed_the_right_version()
    {
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Runner.VersionExit = 1;
        await Refused(offer, UpdateCodes.BadBinary);
    }

    [Fact]
    public async Task A_binary_that_prints_nothing_is_refused()
    {
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Runner.VersionOutput = "";
        await Refused(offer, UpdateCodes.BadBinary);
    }

    [Fact]
    public async Task On_macos_a_binary_without_a_valid_code_signature_is_refused_without_being_run()
    {
        if (!OperatingSystem.IsMacOS()) return;
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Runner.CodesignExit = 1;
        await Refused(offer, UpdateCodes.BadBinary);
        var call = Assert.Single(kit.Runner.Calls);
        Assert.Equal(FakeRunner.Codesign, call.File);
        Assert.Equal(["--verify", "--strict", kit.Staged], call.Args);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_download_that_is_not_200_fails(HttpStatusCode status)
    {
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Host.DownloadStatus = status;
        var result = await Refused(offer, UpdateCodes.Failed);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), result.Message, StringComparison.Ordinal);
    }
}
