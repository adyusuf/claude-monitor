using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>
/// Automatic mode on_message (ADR-0014 P3): an ask from another machine that finds no idle session, names a project this machine knows and has waited
/// <see cref="AgentConfig.ClaudeAutoGrace"/> gets a background worker in that project, so the next pass can hand the ask to it. Only when the machine's owner switched the mode on
/// (the API tells the agent a mode only while its other conditions hold), never a second worker for the same project, at most <see cref="AgentConfig.ClaudeAutoPerHour"/>
/// starts an hour, and no new start for <see cref="AgentConfig.ClaudeAutoBackoff"/> after a failed one. The worker is an ordinary <see cref="ClaudeControl"/> start in the
/// careful manual mode, with no prompt: nothing the ask says reaches `claude`. The ask itself stays where it is.
/// </summary>
public sealed class ClaudeAutoStart(AgentConfig config, ClaudeControl control, LocalStore store, TimeProvider clock, Action<string>? log = null)
{
    public const string Reason = "auto:on_message";
    private readonly Dictionary<string, DateTimeOffset> firstSeen = [];
    private readonly List<DateTimeOffset> starts = [];
    private DateTimeOffset blockedUntil = DateTimeOffset.MinValue;

    public async Task OnUnservedAsync(AskIn ask, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ask);
        if (store.Get(MachineMonitor.ClaudeAutoKey) != ClaudeAutoModes.OnMessage || ask.Project is not { Length: > 0 } project) return;
        var now = clock.GetUtcNow();
        foreach (var gone in firstSeen.Where(kv => now - kv.Value > TimeSpan.FromHours(2)).Select(kv => kv.Key).ToList()) firstSeen.Remove(gone);
        if (!firstSeen.TryAdd(ask.Id, now) && now - firstSeen[ask.Id] >= config.ClaudeAutoGrace)
        {
            if (control.Running().Any(w => w.ProjectKey == project) || now < blockedUntil) return;
            starts.RemoveAll(s => now - s >= TimeSpan.FromHours(1));
            if (starts.Count >= config.ClaudeAutoPerHour) return;
            starts.Add(now);
            var outcome = await control.StartAsync(project, AgentActionKinds.DefaultClaudePermissionMode, null, ct, Reason);
            if (!outcome.Succeeded) blockedUntil = now + config.ClaudeAutoBackoff;
            log?.Invoke(outcome.Succeeded ? "claude worker started for a held ask (automatic mode)" : "claude worker could not be started for a held ask (automatic mode)");
        }
    }
}
