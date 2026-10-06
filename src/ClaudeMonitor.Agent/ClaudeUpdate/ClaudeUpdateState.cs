using System.Text.Json;
using ClaudeMonitor.Agent.Config;

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
}

/// <summary>What the Claude Code updater remembers (claude-update-state.json): the last attempt, its versions, when the next is due.</summary>
public sealed record ClaudeUpdateState(string? CheckedAt = null, string? Result = null, string? Detail = null, string? VersionBefore = null,
    string? VersionAfter = null, string? NextAt = null, string? CountdownUntil = null)
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

    public static void Change(AgentConfig config, Func<ClaudeUpdateState, ClaudeUpdateState> change)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.EnsureHome();
        var temp = config.ClaudeUpdateStatePath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(change(Load(config)), Json));
            File.Move(temp, config.ClaudeUpdateStatePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception d) when (d is IOException or UnauthorizedAccessException)
            {
                // nothing more to do
            }
        }
    }
}
