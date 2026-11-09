using System.Text.Json;
using Podium.Api.Data;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Approvals;

/// <summary>
/// Approval rules (ADR-0019): the scope a request gives a rule, the match when an agent creates a request, and the strict-machine refusal.
/// Only the tools in an allowlist can have a rule, each pinned to one exact value (a command, a path, a search); anything else (an MCP tool,
/// WebSearch, WebFetch, an unknown name, a question, a plan) is answered once. A rule also holds the session's project (repository), so it never acts in another one.
/// </summary>
public static class ApprovalRules
{
    /// <summary>Tools that read one path; their rule covers that exact path (the key that holds it).</summary>
    private static readonly Dictionary<string, string> ReadPathTools = new(StringComparer.Ordinal) { ["Read"] = "file_path", ["LS"] = "path" };

    /// <summary>Tools that search; their rule covers that exact search (every key of the call, in order).</summary>
    private static readonly HashSet<string> SearchTools = new(StringComparer.Ordinal) { "Glob", "Grep" };

    /// <summary>Tools that write one file; their rule covers that exact path, read from the tool's own key only.</summary>
    private static readonly Dictionary<string, string> FileTools = new(StringComparer.Ordinal)
    {
        ["Edit"] = "file_path",
        ["Write"] = "file_path",
        ["MultiEdit"] = "file_path",
        ["NotebookEdit"] = "notebook_path",
    };

    /// <summary>The keys that name a path in a file tool's call: a call that carries the other one gets no rule.</summary>
    private static readonly string[] PathKeys = ["file_path", "notebook_path"];

    /// <summary>The keys of a Bash call that do not change what runs; any other key with a value makes the call one-off.</summary>
    private static readonly HashSet<string> BashQuietKeys = new(StringComparer.Ordinal) { "command", "description", "timeout" };

    /// <summary>
    /// Paths a file rule never covers: what configures Claude, git or a shell runs code later, after the rule has ended (the folder parts are
    /// matched with either separator, the names at the end of the path).
    /// </summary>
    private static readonly string[] GuardedParts = [".claude", ".git", ".vscode", ".idea", ".husky", ".github", ".ssh", "LaunchAgents", "LaunchDaemons"];
    private static readonly string[] GuardedNames =
    [
        ".mcp.json", "settings.json", "settings.local.json", "package.json", ".bashrc", ".bash_profile", ".bash_login", ".profile", ".zshrc",
        ".zprofile", ".zshenv", ".zlogin", "config.fish", "Makefile", ".npmrc", ".envrc", "Microsoft.PowerShell_profile.ps1", "profile.ps1",
        ".gitconfig", "conftest.py", "setup.py", "pyproject.toml", ".pre-commit-config.yaml", "Directory.Build.props", "Directory.Build.targets",
    ];
    private static readonly string[] GuardedEndings = [".sh", ".ps1", ".bat", ".cmd", ".csproj", ".fsproj", ".vbproj", ".targets", ".props"];

    /// <summary>The agent's mark where it masked a secret: the API never sees what stood there, so a masked text can stand for many.</summary>
    public const string MaskedMark = "[masked:";

    /// <summary>
    /// The scope a request gives a rule: its tool and the one value the rule pins (the Bash command, the path, the search).
    /// Null when this request can have no rule: a tool outside the allowlist, a masked or hidden text, a Bash call with a flag that changes how
    /// it runs, or a guarded path.
    /// </summary>
    public static (string Tool, string? Command)? ScopeOf(string toolName, JsonElement input)
    {
        if (string.IsNullOrEmpty(toolName) || input.ValueKind != JsonValueKind.Object) return null;
        if (ReadPathTools.TryGetValue(toolName, out var readKey)) return Plain(input, readKey) is { } read ? (toolName, read) : null;
        if (SearchTools.Contains(toolName)) return Canonical(input) is { } search ? (toolName, search) : null;

        if (string.Equals(toolName, ApprovalRuleLimits.ShellTool, StringComparison.Ordinal))
        {
            foreach (var p in input.EnumerateObject())
            {
                if (!BashQuietKeys.Contains(p.Name) && p.Value.ValueKind is not (JsonValueKind.False or JsonValueKind.Null)) return null;
            }

            return Plain(input, "command") is { } command ? (toolName, command) : null;
        }

        if (FileTools.TryGetValue(toolName, out var key))
        {
            if (PathKeys.Any(k => k != key && input.TryGetProperty(k, out _))) return null;
            var path = Plain(input, key);
            return path is not null && !Guarded(path) ? (toolName, path) : null;
        }

        return null;
    }

    /// <summary>
    /// Whether the call is one of a path tool (ADR-0020 §4): Read, LS and the file tools always, Glob and Grep when they carry a path; and that
    /// path, or null when it is missing or not plain text. False for every other call (Bash, a search in the working folder).
    /// </summary>
    public static bool IsPathCall(string toolName, JsonElement input, out string? path)
    {
        path = null;
        var pathTool = ReadPathTools.TryGetValue(toolName, out var key) || FileTools.TryGetValue(toolName, out key);
        var objectInput = input.ValueKind == JsonValueKind.Object;
        if (!pathTool && !(SearchTools.Contains(toolName) && objectInput && input.TryGetProperty("path", out _))) return false;
        path = objectInput ? Plain(input, key ?? "path") : null;
        return true;
    }

    /// <summary>
    /// A flat call as one canonical text (keys in ordinal order, compact, strings written from their decoded value), so the request the agent
    /// sends and the one read back from the database give the same text. Null for a nested value, a masked or hidden text, or a long one.
    /// </summary>
    private static string? Canonical(JsonElement input)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var p in input.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                w.WritePropertyName(p.Name);
                switch (p.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        var text = p.Value.GetString()!;
                        if (text.Contains(MaskedMark, StringComparison.Ordinal) || TextSafety.HasHidden(text)) return null;
                        w.WriteStringValue(text);
                        break;
                    case JsonValueKind.Number when p.Value.TryGetDecimal(out var number):
                        w.WriteNumberValue(number);
                        break;
                    case JsonValueKind.True or JsonValueKind.False:
                        w.WriteBooleanValue(p.Value.GetBoolean());
                        break;
                    case JsonValueKind.Null:
                        w.WriteNullValue();
                        break;
                    default:
                        return null;
                }
            }

            w.WriteEndObject();
        }

        var canonical = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        return canonical.Length <= ApprovalRuleLimits.CommandMax ? canonical : null;
    }

    /// <summary>A string field as shown to the owner, or null when it is missing, too long, masked, or carries characters that hide what it says.</summary>
    private static string? Plain(JsonElement input, string key)
    {
        if (!input.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var text = v.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > ApprovalRuleLimits.CommandMax || text.Contains(MaskedMark, StringComparison.Ordinal)) return null;
        return TextSafety.HasHidden(text) ? null : text;
    }

    private static bool Guarded(string path)
    {
        var parts = path.Split('/', '\\');
        // A trailing dot or space, or a stream (":"), is the same file on Windows: such a path is never pinned.
        if (parts.Any(p => p == ".." || p.EndsWith('.') && p != "." || p.EndsWith(' ') || p.Contains(':', StringComparison.Ordinal) && p.Length != 2)) return true;
        if (parts.Any(p => GuardedParts.Contains(p, StringComparer.OrdinalIgnoreCase))) return true;
        var name = parts[^1];
        return GuardedNames.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || GuardedEndings.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
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
    /// now, in its workspace, not revoked and not ended, and the machine active and not strict. A path tool's request matches only inside the
    /// project's access (<see cref="ApprovalRuleAccess"/>).
    /// </summary>
    public static async Task<ApprovalRule?> MatchAsync(PodiumDb db, Agent agent, Guid? repoId, string toolName, JsonElement input, string? accessScope,
        DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(agent);
        if (agent.Status != AgentStatuses.Active || Strict(agent) || ScopeOf(toolName, input) is not { } scope) return null;
        if (!await new ApprovalRuleAccess(db).AllowsAsync(agent, repoId, toolName, input, accessScope, ct)) return null;
        return await db.ApprovalRules
            .Where(r => r.AgentId == agent.Id && r.UserId == agent.UserId && r.WorkspaceId == agent.WorkspaceId && r.RepoId == repoId
                && r.ToolName == scope.Tool && r.Command == scope.Command && r.RevokedAt == null && (r.ExpiresAt == null || r.ExpiresAt > now))
            .OrderBy(r => r.CreatedAt).FirstOrDefaultAsync(ct);
    }
}
