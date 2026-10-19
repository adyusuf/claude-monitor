using System.Globalization;
using System.Text;
using System.Text.Json;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.StatusLine;

/// <summary>
/// The usage limits Claude Code showed last (ADR-0011 §7a, limits): the five-hour and seven-day windows of a claude.ai subscription and, behind a Claude apps gateway, the spend limit. Claude Code
/// gives them only to a status line command (<c>rate_limits</c> in its JSON input), which is why <c>aipod statusline</c> exists; this is what it keeps, in a user-only file in the aipod home.
/// A window is use in per cent and the time it resets; nothing else of the payload is kept.
/// </summary>
public sealed record LimitsSnapshot(DateTimeOffset At, ClaudeLimitWindow? FiveHour, ClaudeLimitWindow? SevenDay, ClaudeLimitWindow? Spend)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Any => FiveHour is not null || SevenDay is not null || Spend is not null;

    /// <summary>The limits in a status line payload, or null when the payload has none (an API-key login, or no answer yet in the session): then the file is left as it was.</summary>
    public static LimitsSnapshot? From(JsonElement payload, DateTimeOffset now)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object) return null;
        var snapshot = new LimitsSnapshot(now, Window(limits, "five_hour"), Window(limits, "seven_day"), Window(limits, "spend_limit"));
        return snapshot.Any ? snapshot : null;
    }

    private static ClaudeLimitWindow? Window(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("used_percentage", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var pct) || double.IsNaN(pct)) return null;
        DateTimeOffset? resets = w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        return new ClaudeLimitWindow(Math.Round(Math.Clamp(pct, 0, 1000), 1), resets);
    }

    /// <summary>The short text a status line can show: "5h 23% · 7d 41%".</summary>
    public string Text()
    {
        var parts = new List<string>();
        if (FiveHour is { } five) parts.Add($"5h {five.UsedPercentage.ToString("0", CultureInfo.InvariantCulture)}%");
        if (SevenDay is { } week) parts.Add($"7d {week.UsedPercentage.ToString("0", CultureInfo.InvariantCulture)}%");
        if (Spend is { } spend) parts.Add($"spend {spend.UsedPercentage.ToString("0", CultureInfo.InvariantCulture)}%");
        return string.Join(" · ", parts);
    }

    public void Save(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.EnsureHome();
        var temp = config.ClaudeLimitsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
        File.Move(temp, config.ClaudeLimitsPath, overwrite: true);
    }

    /// <summary>The saved limits, or null when there are none or the file cannot be read.</summary>
    public static LimitsSnapshot? Load(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        try
        {
            var info = new FileInfo(config.ClaudeLimitsPath);
            return info.Exists && info.Length <= 4096 ? JsonSerializer.Deserialize<LimitsSnapshot>(File.ReadAllText(config.ClaudeLimitsPath), Json) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
