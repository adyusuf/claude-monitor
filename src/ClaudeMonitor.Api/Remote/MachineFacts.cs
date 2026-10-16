using System.Text.Json;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// The facts an agent reports in its profile beyond the exec level (ADR-0008 §3a′, §3e): its role claim, Claude presence, the tool
/// inventory and its capabilities. Everything is text from a machine: values outside the closed sets are dropped (never stored),
/// strings are cut and stripped of control characters, lists are capped. A profile without the fields (an older agent) leaves
/// the columns NULL, which the views show as "unknown".
/// </summary>
public static class MachineFacts
{
    public const int ToolsMax = 32;
    public const int PathMax = 260;
    public const int VersionMax = 100;
    public const int NameMax = 32;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Apply(Agent agent, AgentProfile profile, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(profile);
        agent.ClaimedEnvironment = MachineEnvironments.All.Contains(profile.ClaimedEnvironment ?? "") ? profile.ClaimedEnvironment : null;
        agent.ClaimedFunction = MachineFunctions.All.Contains(profile.ClaimedFunction ?? "") ? profile.ClaimedFunction : null;
        if (profile.Claude is { } c && ClaudeKinds.All.Contains(c.Kind))
        {
            agent.ClaudeKind = c.Kind;
            agent.ClaudePath = c.Path is { Length: > 0 } p ? MachineReports.Clean(p, PathMax) : null;
            agent.ClaudeVersion = c.Version is { Length: > 0 } v ? MachineReports.Clean(v, VersionMax) : null;
            agent.ClaudeSessionsLive = Math.Max(0, c.SessionsLive);
            agent.ClaudeSessionsIdle = Math.Clamp(c.SessionsIdle, 0, Math.Max(0, c.SessionsLive));
            agent.PresenceAt = now;
        }
        else
        {
            agent.ClaudeKind = null;
            agent.ClaudePath = null;
            agent.ClaudeVersion = null;
            agent.ClaudeSessionsLive = null;
            agent.ClaudeSessionsIdle = null;
            agent.PresenceAt = profile.Interpreters is null && profile.Tools is null ? null : now;
        }

        agent.Interpreters = profile.Interpreters is null ? null : Serialize(profile.Interpreters.Where(t => t is { Trusted: true } && ToolNames.Interpreters.Contains(t.Name)));
        agent.Tools = profile.Tools is null ? null : Serialize(profile.Tools);
        agent.Capabilities = profile.Capabilities is null ? null : [.. profile.Capabilities.Where(AgentCapabilities.All.Contains).Distinct()];
    }

    /// <summary>
    /// The stored JSON back as tools; NULL stays null (unknown), anything unreadable is an empty list. For a view every member reads, the
    /// path of the `claude` entry is withheld (ADR-0008 §9.9: where Claude Code lives is for the owner and admins only).
    /// </summary>
    public static IReadOnlyList<ToolInfo>? Tools(string? json, bool forView = true)
    {
        if (json is null) return null;
        try
        {
            var tools = JsonSerializer.Deserialize<List<ToolInfo>>(json, Json) ?? [];
            return forView ? tools.Select(t => t.Name == ToolNames.Claude ? t with { Path = "" } : t).ToList() : tools;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Only the names of the list, once each, cleaned and capped; anything else from the machine is dropped.</summary>
    private static string Serialize(IEnumerable<ToolInfo> tools) =>
        JsonSerializer.Serialize(tools.Where(t => t is { Name.Length: > 0, Path.Length: > 0 } && ToolNames.All.Contains(t.Name))
            .GroupBy(t => t.Name, StringComparer.Ordinal).Select(g => g.First()).Take(ToolsMax)
            .Select(t => new ToolInfo(t.Name, MachineReports.Clean(t.Path, PathMax),
                t.Version is { Length: > 0 } v ? MachineReports.Clean(v, VersionMax) : null, t.Trusted)), Json);
}
