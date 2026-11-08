using System.Text.Json;
using Podium.Api.Data;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Approvals;

/// <summary>
/// Approval rules (ADR-0019): the scope a request gives a rule, the match when an agent creates a request, and the strict-machine refusal.
/// Only the tools in an allowlist can have a rule, each with its own scope; anything else (an MCP tool, an unknown name, a question, a plan)
/// is answered once. A rule also holds the session's project (repository), so it never acts in another one.
/// </summary>
public static class ApprovalRules
{
    /// <summary>Tools that only read; their rule covers any input.</summary>
    private static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal) { "Read", "Glob", "Grep", "WebSearch", "LS" };

    /// <summary>Tools that write one file; their rule covers that exact path.</summary>
    private static readonly HashSet<string> FileTools = new(StringComparer.Ordinal) { "Edit", "Write", "MultiEdit", "NotebookEdit" };

    /// <summary>The keys of a Bash call that do not change what runs; any other key with a value makes the call one-off.</summary>
    private static readonly HashSet<string> BashQuietKeys = new(StringComparer.Ordinal) { "command", "description", "timeout" };

    /// <summary>
    /// Paths a file rule never covers: what configures Claude, git or a shell runs code later, after the rule has ended (the folder parts are
    /// matched with either separator, the names at the end of the path).
    /// </summary>
    private static readonly string[] GuardedParts = [".claude", ".git", ".vscode", ".idea", ".husky", ".github"];
    private static readonly string[] GuardedNames =
    [
        ".mcp.json", "settings.json", "settings.local.json", "package.json", ".bashrc", ".bash_profile", ".profile", ".zshrc", ".zprofile",
        ".zshenv", "config.fish", "Makefile", ".npmrc", ".envrc", "Microsoft.PowerShell_profile.ps1", "profile.ps1",
    ];

    /// <summary>The agent's mark where it masked a secret: the API never sees what stood there, so a masked text can stand for many.</summary>
    public const string MaskedMark = "[masked:";

    /// <summary>
    /// The scope a request gives a rule: its tool and the one value the rule pins (the Bash command, the file path; null for a reading tool).
    /// Null when this request can have no rule: a tool outside the allowlist, a masked or hidden text, a Bash call with a flag that changes how
    /// it runs, or a guarded path.
    /// </summary>
    public static (string Tool, string? Command)? ScopeOf(string toolName, JsonElement input)
    {
        if (string.IsNullOrEmpty(toolName) || input.ValueKind != JsonValueKind.Object) return null;
        if (ReadTools.Contains(toolName)) return (toolName, null);
        if (string.Equals(toolName, ApprovalRuleLimits.ShellTool, StringComparison.Ordinal))
        {
            foreach (var p in input.EnumerateObject())
            {
                if (!BashQuietKeys.Contains(p.Name) && p.Value.ValueKind is not (JsonValueKind.False or JsonValueKind.Null)) return null;
            }

            return Plain(input, "command") is { } command ? (toolName, command) : null;
        }

        if (FileTools.Contains(toolName))
        {
            var path = Plain(input, "file_path") ?? Plain(input, "notebook_path");
            return path is not null && !Guarded(path) ? (toolName, path) : null;
        }

        return null;
    }

    /// <summary>A string field as shown to the owner, or null when it is missing, too long, masked, or carries characters that hide what it says.</summary>
    private static string? Plain(JsonElement input, string key)
    {
        if (!input.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var text = v.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > ApprovalRuleLimits.CommandMax || text.Contains(MaskedMark, StringComparison.Ordinal)) return null;
        return text.Any(Hidden) ? null : text;
    }

    /// <summary>Control characters other than a newline or a tab, bidirectional overrides and zero-width characters.</summary>
    private static bool Hidden(char c) =>
        (char.IsControl(c) && c is not ('\n' or '\t')) || c is >= '‪' and <= '‮' || c is >= '⁦' and <= '⁩'
        || c is '​' or '‌' or '‍' or '⁠' or '﻿';

    private static bool Guarded(string path)
    {
        var parts = path.Split('/', '\\');
        if (parts.Any(p => p == ".." || GuardedParts.Contains(p, StringComparer.OrdinalIgnoreCase))) return true;
        var name = parts[^1];
        return GuardedNames.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the machine is strict now (production or database, by its set role or its own claim): no rule is made or matched there.</summary>
    public static bool Strict(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return MachineRoles.IsStrict(MachineRoles.EffectiveEnvironment(agent.RoleEnvironment, agent.ClaimedEnvironment),
            MachineRoles.EffectiveFunction(agent.RoleFunction, agent.ClaimedFunction));
    }

    /// <summary>
    /// The live rule that allows this request, if any: the same machine, project, tool and pinned value, made by the user who owns the machine
    /// now, in its workspace, not revoked and not ended, and the machine active and not strict.
    /// </summary>
    public static async Task<ApprovalRule?> MatchAsync(PodiumDb db, Agent agent, Guid? repoId, string toolName, JsonElement input, DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(agent);
        if (agent.Status != AgentStatuses.Active || Strict(agent) || ScopeOf(toolName, input) is not { } scope) return null;
        return await db.ApprovalRules
            .Where(r => r.AgentId == agent.Id && r.UserId == agent.UserId && r.WorkspaceId == agent.WorkspaceId && r.RepoId == repoId
                && r.ToolName == scope.Tool && r.Command == scope.Command && r.RevokedAt == null && (r.ExpiresAt == null || r.ExpiresAt > now))
            .OrderBy(r => r.CreatedAt).FirstOrDefaultAsync(ct);
    }
}
