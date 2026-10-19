using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// What Claude Code has on a machine, as its agent reported it (ADR-0011 §7a, step E): sessions, plugins, skill names and the adyusuf settings of project folders. It is text
/// from a machine, so it is checked again here: lists are capped, strings are cut and stripped of control characters, ids, words and gate names must fit a closed shape, a mode
/// is one letter A to E. It is stored as one JSON document, shown to the machine's owner and to admins only, and replaced by every profile (a profile without it leaves the column NULL).
/// </summary>
public static partial class ClaudeInventoryFacts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Statuses = ["idle", "busy", "waiting"];

    [GeneratedRegex(@"^[A-Za-z0-9._@:+-]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdShape();

    [GeneratedRegex(@"^[a-z][a-z0-9_-]{0,19}$", RegexOptions.CultureInvariant)]
    private static partial Regex WordShape();

    [GeneratedRegex(@"^[a-z0-9_]{1,30}(\.[a-z0-9_]{1,30}){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex GateShape();

    public static void Apply(Agent agent, AgentProfile profile, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Inventory is not { } inventory)
        {
            agent.ClaudeInventory = null;
            agent.ClaudeInventoryAt = null;
            return;
        }

        agent.ClaudeInventory = JsonSerializer.Serialize(Clean(inventory, now), Json);
        agent.ClaudeInventoryAt = now;
    }

    public static ClaudeInventory? From(string? json, DateTimeOffset? at)
    {
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<ClaudeInventory>(json, Json) is { } inventory ? inventory with { At = at ?? inventory.At } : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static ClaudeInventory Clean(ClaudeInventory i, DateTimeOffset now) => new(
        i.Sessions?.Where(s => s is not null).Take(InventoryLimits.SessionsMax).Select(s => new InventorySession(Text(s.Name, InventoryLimits.NameMax), Text(s.ProjectKey, InventoryLimits.KeyMax),
            s.Kind is { } k && WordShape().IsMatch(k) ? k : null, s.Status is { } st && Statuses.Contains(st) ? st : null, s.StartedAt)).ToList(),
        i.Plugins?.Where(p => p is not null && p.Id is { } id && IdShape().IsMatch(id)).Take(InventoryLimits.PluginsMax)
            .Select(p => new InventoryPlugin(p.Id, Text(p.Version, 64), p.Enabled, p.Scope is { } sc && WordShape().IsMatch(sc) ? sc : null)).ToList(),
        i.Skills?.Where(s => s is not null && IdShape().IsMatch(s)).Distinct(StringComparer.Ordinal).Take(InventoryLimits.SkillsMax).ToList(),
        i.Settings?.Where(s => s is not null && Text(s.ProjectKey, InventoryLimits.KeyMax) is not null).Take(InventoryLimits.SettingsMax).Select(s => new InventorySetting(
            Text(s.ProjectKey, InventoryLimits.KeyMax)!, s.Mode is { Length: 1 } m && m[0] is >= 'A' and <= 'E' ? m : null,
            s.ModeModel is { } mm && WordShape().IsMatch(mm) ? mm : null,
            s.Gates?.Where(g => GateShape().IsMatch(g.Key)).Take(InventoryLimits.GatesMax).ToDictionary(g => g.Key, g => g.Value, StringComparer.Ordinal) is { Count: > 0 } gates ? gates : null)).ToList(),
        now);

    private static string? Text(string? text, int max)
    {
        var cleaned = text is null ? "" : MachineReports.Clean(text, max).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
