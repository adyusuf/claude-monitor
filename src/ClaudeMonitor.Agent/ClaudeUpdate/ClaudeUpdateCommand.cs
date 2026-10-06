using System.Globalization;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;

namespace ClaudeMonitor.Agent.ClaudeUpdate;

/// <summary>"cm-agent claude-update cancel", "cm-agent config claude-update on|off", and the lines `cm-agent status` adds.</summary>
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
            await stderr.WriteLineAsync("usage: cm-agent claude-update cancel");
            return 2;
        }

        var waiting = DateTimeOffset.TryParse(ClaudeUpdateState.Load(config).CountdownUntil, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until)
                      && until > clock.GetUtcNow();
        if (!waiting)
        {
            await stdout.WriteLineAsync("no Claude Code update is waiting to start");
            return 0;
        }

        config.EnsureHome();
        await File.WriteAllTextAsync(config.ClaudeCancelPath, clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        await stdout.WriteLineAsync($"the Claude Code update is cancelled; it is not tried again for {config.ClaudeSnooze.TotalHours:0} h");
        return 0;
    }

    public static IEnumerable<string> Describe(AgentConfig config, LocalStore store, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        var machine = Config.SavedSettings.Apply(config).ClaudeUpdateEnabled;
        var workspace = store.Get(ClaudePolicy.WorkspaceKey) == "true";
        yield return $"claude-update: {(machine && workspace ? "on" : "off")} (this machine: {(machine ? "on" : "off")}, workspace: {(workspace ? "on" : "off")})";
        if (!machine && !workspace) yield break;
        var state = ClaudeUpdateState.Load(config);
        if (DateTimeOffset.TryParse(state.CountdownUntil, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until) && until > clock.GetUtcNow())
        {
            yield return $"Claude Code will be updated at {until.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)} (cancel: cm-agent claude-update cancel)";
        }

        if (state.Result is { } result)
        {
            var at = DateTimeOffset.TryParse(state.CheckedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
                ? when.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
                : "?";
            yield return $"last Claude Code update: {result} ({state.Detail}) {at}";
        }
    }
}
