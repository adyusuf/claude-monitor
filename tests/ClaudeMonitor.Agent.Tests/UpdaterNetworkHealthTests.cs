using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>
/// How a new daemon is judged when the API has not answered it: a network failure says nothing against the build (it stays,
/// not confirmed); a refusal by the API, a crash, a missing or wrong version, or silence are a rollback. And only a heartbeat
/// from after the old daemon stopped counts.
/// </summary>
public sealed class UpdaterNetworkHealthTests : IDisposable
{
    private UpdateKit kit = new(healthy: false);

    public void Dispose() => kit.Dispose();

    private string Bin => "start:" + kit.Config.BinaryPath;

    private UpdateOffer Prepare()
    {
        kit.InstallOld();
        return kit.PublishGood();
    }

    private async Task<UpdateResult> ApplyWithNewDaemon(Action<FakeDaemon, UpdateOffer> onStart)
    {
        var offer = Prepare();
        kit.Daemon.OnStart = d => onStart(d, offer);
        return await kit.Updater.ApplyAsync(offer, CancellationToken.None);
    }

    private void AssertRolledBack(UpdateResult result, string version)
    {
        Assert.Equal(UpdateCodes.RolledBack, result.Code);
        Assert.Equal(UpdateKit.OldBytes, File.ReadAllBytes(kit.Config.BinaryPath));
        Assert.Equal(version, kit.State.BlockedVersion);
        Assert.Null(kit.State.Phase);
        Assert.Null(kit.State.InstalledAt);
        Assert.Equal(["stop", Bin, "stop", Bin], kit.Daemon.Calls);
    }

    // ---- a network failure is not a verdict against the build ------------------------------------------------

    [Theory]
    [InlineData("HttpRequestException")]
    [InlineData("TimeoutException")]
    [InlineData("SocketException")]
    [InlineData("IOException")]
    public async Task A_running_new_daemon_whose_heartbeats_fail_on_the_network_stays_installed_but_not_confirmed(string error)
    {
        var result = await ApplyWithNewDaemon((d, offer) => d.Report(offer.Version, contact: false, error: error));

        Assert.Equal(UpdateCodes.Installed, result.Code);
        Assert.Contains("not confirmed", result.Message, StringComparison.Ordinal);
        Assert.Equal("NEW-BINARY", File.ReadAllText(kit.Config.BinaryPath)); // the new bytes stay
        Assert.Equal(UpdateKit.OldBytes, File.ReadAllBytes(kit.Previous)); // and the old ones stay beside them
        Assert.Equal(["stop", Bin], kit.Daemon.Calls); // no rollback stop, no second start
        Assert.Equal(0, kit.Daemon.Kills);
        Assert.True(((VirtualClock)kit.Clock).Elapsed >= kit.Config.UpdateHealthWait, "it waited the whole health window first");
        var state = kit.State;
        Assert.Equal(UpdateCodes.Installed, state.Result);
        Assert.Null(state.BlockedVersion);
        Assert.Null(state.Phase);
        Assert.NotNull(state.InstalledAt);
    }

    // ---- everything else is a rollback ----------------------------------------------------------------------

    [Fact]
    public async Task A_new_daemon_the_api_refused_is_rolled_back()
    {
        var result = await ApplyWithNewDaemon((d, offer) => d.Report(offer.Version, contact: false, error: "ApiException"));
        AssertRolledBack(result, result.To!);
    }

    [Theory]
    [InlineData(null)] // it never wrote a failure
    [InlineData("")] // the last heartbeat was answered once, long ago: nothing says "the network"
    public async Task A_new_daemon_nothing_has_answered_and_that_names_no_network_failure_is_rolled_back(string? error)
    {
        var result = await ApplyWithNewDaemon((d, offer) => d.Report(offer.Version, contact: false, error: error));
        AssertRolledBack(result, result.To!);
    }

    [Fact]
    public async Task A_new_daemon_that_never_reported_its_version_is_rolled_back_even_with_a_network_error()
    {
        var result = await ApplyWithNewDaemon((d, _) => d.Report(null, contact: false, error: "HttpRequestException"));
        AssertRolledBack(result, result.To!);
    }

    [Fact]
    public async Task A_daemon_that_reports_the_old_version_is_rolled_back_even_with_a_network_error()
    {
        var result = await ApplyWithNewDaemon((d, _) => d.Report(AgentConfig.Version, contact: false, error: "HttpRequestException"));
        AssertRolledBack(result, result.To!);
    }

    [Fact]
    public async Task A_new_daemon_that_is_gone_at_the_deadline_is_rolled_back_even_if_it_blamed_the_network()
    {
        var result = await ApplyWithNewDaemon((d, offer) =>
        {
            d.Report(offer.Version, contact: false, error: "HttpRequestException");
            if (d.Starts == 1) d.Running = false; // it crashed after writing that (only the update's own start)
        });
        AssertRolledBack(result, result.To!);
    }

    // ---- the old daemon's last heartbeat does not count ------------------------------------------------------

    private async Task<(UpdateResult Result, UpdateOffer Offer)> ApplyWhereTheOldDaemonAnswersWhileStopping(bool newDaemonAnswers)
    {
        kit.Dispose();
        var clock = new VirtualClock(DateTimeOffset.UtcNow);
        kit = new UpdateKit(clock: clock, healthy: newDaemonAnswers);
        var offer = Prepare();
        kit.Daemon.OnStop = d =>
        {
            if (d.Calls.Count(c => c == "stop") != 1) return; // only the update's own stop, not a rollback's
            d.WriteHealthMarkers(); // after the swap, before the new daemon starts: the OLD daemon's last words, with the new version
            clock.Advance(TimeSpan.FromSeconds(1)); // time passes before the updater notes when the new daemon started
        };
        return (await kit.Updater.ApplyAsync(offer, CancellationToken.None), offer);
    }

    [Fact]
    public async Task A_heartbeat_written_while_the_old_daemon_was_stopping_does_not_make_the_new_one_healthy()
    {
        var (result, offer) = await ApplyWhereTheOldDaemonAnswersWhileStopping(newDaemonAnswers: false);
        AssertRolledBack(result, offer.Version);
    }

    [Fact]
    public async Task The_same_timing_with_a_new_daemon_that_does_answer_is_healthy()
    {
        var (result, _) = await ApplyWhereTheOldDaemonAnswersWhileStopping(newDaemonAnswers: true);
        Assert.Equal(UpdateCodes.Installed, result.Code);
        Assert.Contains("healthy", result.Message, StringComparison.Ordinal);
        Assert.Equal(["stop", Bin], kit.Daemon.Calls);
    }
}
