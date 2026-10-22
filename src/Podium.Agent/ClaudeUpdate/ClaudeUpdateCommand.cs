using System.Globalization;
using Podium.Agent.Config;
using Podium.Agent.Storage;

namespace Podium.Agent.ClaudeUpdate;

/// <summary>"aipod claude-update cancel", "aipod config claude-update on|off", and the lines `aipod status` adds.</summary>
public static class ClaudeUpdateCommand
{
    public const string Verb = "claude-update";

    public static async Task<int> RunAsync(string[] args, AgentConfig config, TextWriter stdout, TextWriter stderr, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(clock);
        if (args.Length != 2 || args[1] != "cancel")
        {
            await stderr.WriteLineAsync(Strings.T(Msg.ClaudeUpdateUsage));
            return 2;
        }

        var waiting = DateTimeOffset.TryParse(ClaudeUpdateState.Load(config).CountdownUntil, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until)
                      && until > clock.GetUtcNow();
        if (!waiting)
        {
            await stdout.WriteLineAsync(Strings.T(Msg.ClaudeUpdateNone));
            return 0;
        }

        config.EnsureHome();
        await File.WriteAllTextAsync(config.ClaudeCancelPath, clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        await stdout.WriteLineAsync(Strings.T(Msg.ClaudeUpdateCancelled, config.ClaudeSnooze.TotalHours.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
        return 0;
    }

    private static string OnOff(bool on) => Strings.T(on ? Msg.CuOn : Msg.CuOff);

    public static IEnumerable<string> Describe(AgentConfig config, LocalStore store, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        var machine = Config.SavedSettings.Apply(config).ClaudeUpdateEnabled;
        var answer = Daemon.WorkspaceSettings.Get(config, store, ClaudePolicy.WorkspaceKey);
        var workspace = answer == "true";
        yield return Strings.T(Msg.CuLine, OnOff(machine && workspace), OnOff(machine), answer is null ? Daemon.WorkspaceSettings.Unread : OnOff(workspace));
        if (!machine && !workspace) yield break;
        var state = ClaudeUpdateState.Load(config);
        if (DateTimeOffset.TryParse(state.CountdownUntil, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until) && until > clock.GetUtcNow())
        {
            yield return Strings.T(Msg.CuWill, until.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));
        }

        if (state.Result is { } result)
        {
            var at = DateTimeOffset.TryParse(state.CheckedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
                ? when.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
                : "?";
            yield return Strings.T(Msg.CuLast, result, state.Detail, at);
        }

        if (state.Failures >= config.ClaudeFailuresBeforeBackoff)
        {
            yield return Strings.T(Msg.CuFailing, state.Failures, config.ClaudeUpdateEvery.TotalHours);
        }
    }
}
