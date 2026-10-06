using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Update;

/// <summary>
/// The daemon's update chore. It wakes every <see cref="AgentConfig.UpdatePollEvery"/> (so a changed setting, or the
/// workspace's cap arriving after the daemon started, is noticed soon) but looks at the server only once per
/// <see cref="AgentConfig.UpdateCheckEvery"/>. With the effective mode off it does nothing at all, not even a network call;
/// "check" looks and remembers; "on" also starts the install, which runs in its own process (it stops and replaces this daemon).
/// </summary>
public sealed class UpdateLoop(AgentConfig config, LocalStore store, Updater updater, Func<bool> startInstall, TimeProvider clock)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var mode = UpdatePolicy.Effective(SavedSettings.Apply(config), store); // the saved setting is read afresh: this daemon may be old
        if (mode == UpdateModes.Off) return;
        if (!Due(UpdateState.Load(config))) return;
        try
        {
            var check = await updater.CheckAsync(ct);
            if (mode == UpdateModes.On && check.Offer is not null && MayStart(UpdateState.Load(config), check.Offer.Version)) startInstall();
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException or IOException or Net.ApiException)
        {
            // already recorded by CheckAsync; the next round tries again at the normal interval, never in a tight retry loop
        }
    }

    private bool Due(UpdateState state) =>
        !(DateTimeOffset.TryParse(state.CheckedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
          && clock.GetUtcNow() - at < config.UpdateCheckEvery);

    /// <summary>Not while one is in progress, not a build that was rolled back, and not again at once after a failed attempt.</summary>
    internal bool MayStart(UpdateState state, string version) =>
        state.Phase != UpdateState.PendingHealth
        && state.BlockedVersion != version
        && !(DateTimeOffset.TryParse(state.AttemptedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
             && clock.GetUtcNow() - at < config.UpdateRetryAfter);
}
