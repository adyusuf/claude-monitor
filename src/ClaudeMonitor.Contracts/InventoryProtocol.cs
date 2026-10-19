namespace ClaudeMonitor.Contracts;

// What Claude Code has on a machine, read-only (ADR-0011 §7a, step E). Like the other protocol files it only GROWS (global #4).

/// <summary>One Claude Code session on the machine: its name (text a person or Claude wrote), the project key of its folder, how it was started and its state.</summary>
public sealed record InventorySession(string? Name, string? ProjectKey, string? Kind, string? Status, DateTimeOffset? StartedAt);

/// <summary>An installed plugin as <c>claude plugin list --json</c> says it: never the install path.</summary>
public sealed record InventoryPlugin(string Id, string? Version, bool Enabled, string? Scope);

/// <summary>
/// The working mode, model override and yes/no gates (<c>merge.dev</c>, <c>push.prod</c>, <c>run_before_push.test.unit</c>, ...) the adyusuf plugin keeps in a project folder
/// (<c>.claude/mode</c>, <c>.claude/mode-model</c>, <c>.claude/adyusuf.json</c>). Only values that fit a closed shape are reported; nothing is read as free text.
/// </summary>
public sealed record InventorySetting(string ProjectKey, string? Mode, string? ModeModel, IReadOnlyDictionary<string, bool>? Gates);

/// <summary>Everything the agent read, at <paramref name="At"/>: sessions, plugins, skill names (folder names only) and the project settings. A list the agent could not read is null.</summary>
public sealed record ClaudeInventory(IReadOnlyList<InventorySession>? Sessions, IReadOnlyList<InventoryPlugin>? Plugins, IReadOnlyList<string>? Skills,
    IReadOnlyList<InventorySetting>? Settings, DateTimeOffset At, ClaudeLimits? Limits = null);

/// <summary>One usage window of Claude Code: use in per cent (0 to 100, or above for a spend limit past its limit) and when the window resets.</summary>
public sealed record ClaudeLimitWindow(double UsedPercentage, DateTimeOffset? ResetsAt);

/// <summary>The five-hour and seven-day windows of a claude.ai subscription and, behind a Claude apps gateway, the spend limit, as Claude Code last gave them to a status line command; <paramref name="SeenAt"/> is when.</summary>
public sealed record ClaudeLimits(ClaudeLimitWindow? FiveHour, ClaudeLimitWindow? SevenDay, ClaudeLimitWindow? Spend, DateTimeOffset SeenAt);

public static class InventoryLimits
{
    public const int SessionsMax = 50;
    public const int PluginsMax = 100;
    public const int SkillsMax = 300;
    public const int SettingsMax = 50;
    public const int GatesMax = 40;
    public const int NameMax = 120;
    public const int IdMax = 200;
    public const int KeyMax = 100;
}
