using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Remote;

/// <summary>What a remote write was checked to be: its (cleaned) fields, or why it is refused.</summary>
public sealed record ConfigCheck(string? ProjectKey, string? Target, string? Value, IResult? Refusal);

/// <summary>
/// The shape checks of the remote writes (ADR-0011 §7a): a plugin id as <c>name@marketplace</c> for enable, disable, update and install, and for a project setting a project the machine reported, a setting name
/// from the closed list and a value that fits it. The rules about who may ask, where and when are the ones of a start of Claude from the web, checked before this in <see cref="AgentActions"/>.
/// </summary>
public static class ConfigActions
{
    public static async Task<ConfigCheck> CheckAsync(PodiumDb db, Guid agentId, string kind, AgentActionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(req);
        if (req.FirstPrompt is { Length: > 0 } || req.PermissionMode is not null || req.SessionRef is not null) return Invalid("target", AgentActionErrors.InvalidSetting);
        if (kind != AgentActionKinds.ProjectSetting)
        {
            if (req.ProjectKey is not null || req.Value is not null) return Invalid("target", AgentActionErrors.InvalidPlugin);
            return ClaudeConfigRules.IsPluginId(req.Target) ? new(null, req.Target, null, null) : Invalid("target", AgentActionErrors.InvalidPlugin);
        }

        if (req.ProjectKey is not { Length: >= 1 and <= RunKinds.ProjectKeyMax } project || project.Any(char.IsControl)) return Invalid("projectKey", AgentActionErrors.InvalidProject);
        if (!ClaudeConfigRules.SettingNames.Contains(req.Target ?? "") || !ClaudeConfigRules.IsSettingValue(req.Target, req.Value)) return Invalid("value", AgentActionErrors.InvalidSetting);
        if (!await db.MachineProjects.AsNoTracking().AnyAsync(p => p.AgentId == agentId && p.ProjectKey == project, ct)) return Invalid("projectKey", AgentActionErrors.InvalidProject);
        return new(project, req.Target, req.Value, null);
    }

    private static ConfigCheck Invalid(string field, string code) => new(null, null, null, Http.Invalid(field, code));
}
