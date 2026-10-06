using System.Text.Json;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;

namespace ClaudeMonitor.Agent.ClaudeUpdate;

public static class ClaudeCodes
{
    public const string Updated = "updated";
    public const string Unchanged = "unchanged";
    public const string Waiting = "waiting";
    public const string Cancelled = "cancelled";
    public const string NoClaude = "no-claude";
    public const string Unsupported = "unsupported-install";
    public const string Failed = "failed";
    public const string Disabled = "disabled";
    public const string NotDue = "not-due";
    public const string Busy = "busy";

    /// <summary>The countdown is running (an attempt holds the lock).</summary>
    public const string Countdown = "countdown";

    /// <summary>The agent stopped during a countdown or while `claude update` ran (then it was killed).</summary>
    public const string Interrupted = "interrupted";

    /// <summary>claude-update-state.json could not be written, so the attempt was given up before anything ran.</summary>
    public const string NotSaved = "state-not-saved";
}

/// <summary>What the Claude Code updater remembers (claude-update-state.json): the last attempt, its versions, when the next is due.</summary>
/// <param name="Failures">Failed attempts in a row (`claude update` did not exit 0); an update or an up-to-date answer resets it.</param>
public sealed record ClaudeUpdateState(string? CheckedAt = null, string? Result = null, string? Detail = null, string? VersionBefore = null,
    string? VersionAfter = null, string? NextAt = null, string? CountdownUntil = null, int Failures = 0)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static ClaudeUpdateState Load(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        try
        {
            return File.Exists(config.ClaudeUpdateStatePath) ? JsonSerializer.Deserialize<ClaudeUpdateState>(File.ReadAllText(config.ClaudeUpdateStatePath), Json) ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>Writes the changed state; false when it could not be written (logged with the error's type only, never the content).</summary>
    public static bool Change(AgentConfig config, Func<ClaudeUpdateState, ClaudeUpdateState> change, AgentLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(change);
        var temp = config.ClaudeUpdateStatePath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            config.EnsureHome();
            File.WriteAllText(temp, JsonSerializer.Serialize(change(Load(config)), Json));
            File.Move(temp, config.ClaudeUpdateStatePath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var left = "";
            try
            {
                File.Delete(temp);
            }
            catch (Exception d) when (d is IOException or UnauthorizedAccessException)
            {
                left = $"; its temporary file could not be removed ({d.GetType().Name})";
            }

            log?.Write($"claude update: {Path.GetFileName(config.ClaudeUpdateStatePath)} could not be saved ({e.GetType().Name}){left}");
            return false;
        }
    }
}
