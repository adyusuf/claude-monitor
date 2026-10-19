using System.Text.RegularExpressions;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// The projects on a machine (ADR-0008 §3a′): one row per (agent, project key), from the project key every session reports and from the
/// folder facts the agent reads out of <c>.git</c> files. Everything is text from a machine: the key and the facts are cut and stripped of
/// control characters, a commit must be hex, the path HMAC is an opaque hex string, a list is capped.
/// </summary>
public static partial class MachineProjects
{
    public const int FactsMax = 200;

    /// <summary>A session reported this project on this machine: the row exists, with the newest sighting and the branch the session was on.</summary>
    public static async Task SeenInSessionAsync(MonitorDb db, Guid agentId, Guid workspaceId, string key, string name, string? branch,
        DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!KeyOk(key)) return;
        var row = db.MachineProjects.Local.FirstOrDefault(p => p.AgentId == agentId && p.ProjectKey == key)
                  ?? await db.MachineProjects.FirstOrDefaultAsync(p => p.AgentId == agentId && p.ProjectKey == key, ct);
        if (row is null)
        {
            row = new MachineProject { AgentId = agentId, ProjectKey = key, WorkspaceId = workspaceId, Name = MachineReports.Clean(name, 200), Source = ProjectSources.Session };
            db.MachineProjects.Add(row);
        }

        if (at > row.LastSeenAt) row.LastSeenAt = at;
        if (branch is { Length: > 0 } && row.Source == ProjectSources.Session) row.Branch = MachineReports.Clean(branch, 200);
    }

    /// <summary>Folds the facts of a profile into the machine's rows: the agent's reading of a folder wins over what a session said.</summary>
    public static async Task ApplyAsync(MonitorDb db, Guid agentId, Guid workspaceId, IReadOnlyList<ProjectFact>? facts, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (facts is null) return;
        foreach (var fact in facts.Where(f => f is not null && KeyOk(f.Key)).GroupBy(f => f.Key, StringComparer.Ordinal).Select(g => g.First()).Take(FactsMax))
        {
            var row = db.MachineProjects.Local.FirstOrDefault(p => p.AgentId == agentId && p.ProjectKey == fact.Key)
                      ?? await db.MachineProjects.FirstOrDefaultAsync(p => p.AgentId == agentId && p.ProjectKey == fact.Key, ct);
            if (row is null)
            {
                row = new MachineProject { AgentId = agentId, ProjectKey = fact.Key, WorkspaceId = workspaceId };
                db.MachineProjects.Add(row);
            }

            row.Name = MachineReports.Clean(fact.Name, 200);
            row.PathHmac = fact.PathHmac is { Length: > 0 and <= 128 } h && Hex().IsMatch(h) ? h : null;
            row.Branch = fact.Branch is { Length: > 0 } b ? MachineReports.Clean(b, 200) : null;
            row.HeadSha = fact.HeadSha is { Length: > 0 and <= 64 } sha && Hex().IsMatch(sha) ? sha : null;
            row.Tag = fact.Tag is { Length: > 0 } t ? MachineReports.Clean(t, 64) : null;
            row.Source = ProjectSources.All.Contains(fact.Source) ? fact.Source : ProjectSources.Listed;
            row.LastSeenAt = now;
            row.ClaudeTrusted = fact.Trusted;
        }
    }

    private static bool KeyOk(string? key) => key is { Length: > 0 } && key.Length <= RunKinds.ProjectKeyMax && !key.Any(char.IsControl);

    [GeneratedRegex("^[0-9a-fA-F]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Hex();
}
