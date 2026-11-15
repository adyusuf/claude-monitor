using Podium.Agent.Update;

namespace Podium.Agent.Tests;

/// <summary>F-319: a download that outlives <c>UpdateDownloadTimeout</c> is a recorded failure, never an unhandled exception.</summary>
public sealed class UpdaterDownloadTimeoutTests : IDisposable
{
    private readonly UpdateKit kit = new(c => c with { UpdateDownloadTimeout = TimeSpan.FromMilliseconds(200) });

    public void Dispose() => kit.Dispose();

    [Fact]
    public async Task A_stalled_download_past_the_deadline_is_recorded_as_failed_and_leaves_the_installation_as_it_was()
    {
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Host.StallBody = true;

        var result = await kit.ApplyRefusedAsync(offer, UpdateCodes.Failed);

        Assert.Contains("did not finish", result.Message, StringComparison.Ordinal);
        Assert.Single(kit.Host.Downloads);
        Assert.Empty(kit.Runner.Calls); // nothing half-downloaded was ever started
        Assert.Contains("update failed", File.ReadAllText(kit.Config.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_download_the_caller_cancels_is_still_ended_by_the_caller_not_recorded_as_a_timeout()
    {
        kit.InstallOld();
        var offer = kit.PublishGood();
        kit.Host.StallBody = true;
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => kit.Updater.ApplyAsync(offer, caller.Token));

        Assert.NotEqual(UpdateCodes.Failed, kit.State.Result);
    }
}
