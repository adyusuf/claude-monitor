using System.Text.Json;
using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.ClaudeUpdate;

/// <summary>One live Claude Code session as its own file describes it. Status is "idle", "busy" or "waiting"; anything else is unknown.</summary>
public sealed record ClaudeSession(int Pid, string? Status, DateTimeOffset? StatusAt);

public sealed record SessionsView(bool Readable, IReadOnlyList<ClaudeSession> Live, string Why);

/// <summary>
/// Which Claude Code sessions run, and whether all are idle: Claude Code writes sessions/&lt;pid&gt;.json (pid, status, the time
/// the status last changed in epoch milliseconds) for every running session, whichever way it was started. The format is
/// Claude Code's own and undocumented, so every doubt reads as "not idle": a missing folder, an unreadable file, a status that is
/// not exactly "idle", a missing time.
/// </summary>
public static class ClaudeSessions
{
    public static SessionsView Read(AgentConfig config, Func<int, bool>? alive = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        alive ??= ProcessExists;
        var dir = Path.Combine(config.ClaudeConfigDir, "sessions");
        if (!Directory.Exists(dir)) return new(false, [], "the sessions folder of Claude Code is not there, so no session can be seen");
        var live = new List<ClaudeSession>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                if (ReadOne(file) is not { } session)
                {
                    return new(false, [], $"{Path.GetFileName(file)} could not be read");
                }

                if (alive(session.Pid)) live.Add(session);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(false, [], $"the sessions folder could not be read: {e.GetType().Name}");
        }

        return new(true, live, "");
    }

    /// <summary>True only when the sessions are readable and every live one is idle and has been for at least <paramref name="idleFor"/>.</summary>
    public static (bool Idle, string Why) AllIdle(SessionsView view, DateTimeOffset now, TimeSpan idleFor)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (!view.Readable) return (false, view.Why);
        foreach (var s in view.Live)
        {
            if (s.Status != "idle") return (false, $"a session is {(s.Status is "busy" or "waiting" ? s.Status : "in a state this agent does not know")}");
            if (s.StatusAt is not { } at || now - at < idleFor) return (false, $"a session was active less than {idleFor.TotalMinutes:0} min ago");
        }

        return (true, view.Live.Count == 0 ? "no session is running" : $"{view.Live.Count} session(s), all idle");
    }

    private static ClaudeSession? ReadOne(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("pid", out var pid) || pid.ValueKind != JsonValueKind.Number
                || !pid.TryGetInt32(out var id)) return null;
            var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            DateTimeOffset? at = null;
            foreach (var name in new[] { "statusUpdatedAt", "updatedAt" })
            {
                if (root.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var ms) && ms > 0)
                {
                    at = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                    break;
                }
            }

            return new ClaudeSession(id, status, at);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool ProcessExists(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
