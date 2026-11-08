using System.Text.Json;
using Podium.Api.Data;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Approvals;

/// <summary>
/// Approval rules (ADR-0019): the scope a request gives a rule, the match when an agent creates a request, and the strict-machine refusal.
/// A rule is the owner's allow for one tool on one machine; for <c>Bash</c> it also holds the exact command.
/// </summary>
public static class ApprovalRules
{
    /// <summary>The scope a request gives a rule: its tool and, for <c>Bash</c>, its exact command. Null when this request can have no rule.</summary>
    public static (string Tool, string? Command)? ScopeOf(string toolName, JsonElement input)
    {
        if (string.IsNullOrEmpty(toolName) || ApprovalRuleLimits.NeverRemembered.Contains(toolName)) return null;
        if (!string.Equals(toolName, ApprovalRuleLimits.ShellTool, StringComparison.Ordinal)) return (toolName, null);
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("command", out var c) || c.ValueKind != JsonValueKind.String) return null;
        var command = c.GetString();
        return string.IsNullOrWhiteSpace(command) || command.Length > ApprovalRuleLimits.CommandMax || command.Contains('\0') ? null : (toolName, command);
    }

    /// <summary>True when the machine is strict now (production or database, by its set role or its own claim): no rule is made or matched there.</summary>
    public static bool Strict(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return MachineRoles.IsStrict(MachineRoles.EffectiveEnvironment(agent.RoleEnvironment, agent.ClaimedEnvironment),
            MachineRoles.EffectiveFunction(agent.RoleFunction, agent.ClaimedFunction));
    }

    /// <summary>
    /// The live rule that allows this request, if any: the same machine, tool and (for <c>Bash</c>) command, made by the user who owns the machine
    /// now, in its workspace, not revoked and not ended, and the machine active and not strict.
    /// </summary>
    public static async Task<ApprovalRule?> MatchAsync(PodiumDb db, Agent agent, string toolName, JsonElement input, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(agent);
        if (agent.Status != AgentStatuses.Active || Strict(agent) || ScopeOf(toolName, input) is not { } scope) return null;
        return await db.ApprovalRules
            .Where(r => r.AgentId == agent.Id && r.UserId == agent.UserId && r.WorkspaceId == agent.WorkspaceId && r.ToolName == scope.Tool
                && r.Command == scope.Command && r.RevokedAt == null && (r.ExpiresAt == null || r.ExpiresAt > now))
            .OrderBy(r => r.CreatedAt).FirstOrDefaultAsync(ct);
    }
}
