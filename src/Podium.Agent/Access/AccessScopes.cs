using System.Text.Json;
using Microsoft.Data.Sqlite;
using Podium.Agent.Auth;
using Podium.Agent.Config;
using Podium.Agent.Exec;
using Podium.Agent.Storage;
using Podium.Contracts;
using Scopes = Podium.Contracts.AccessScopes;

namespace Podium.Agent.Access;

/// <summary>
/// Where a path tool's target lies against the session's project layers (ADR-0019 tie-in, ADR-0020 §4): <c>inside</c> when every layer's folders
/// allow it, <c>outside</c> when one refuses it, <c>unknown</c> when it cannot be judged (no absolute path, a path that cannot be resolved or read).
/// The API makes or matches an approval rule for a path only when the agent says inside, so anything doubtful is never inside. Null for a tool that
/// names no path, as from an older agent.
/// </summary>
public static class AccessScopes
{
    /// <summary>The path tools and the input field that names their target.</summary>
    private static readonly Dictionary<string, string> PathField = new(StringComparer.Ordinal)
    {
        ["Read"] = "file_path",
        ["Edit"] = "file_path",
        ["Write"] = "file_path",
        ["MultiEdit"] = "file_path",
        ["NotebookEdit"] = "notebook_path",
        ["LS"] = "path",
        ["Glob"] = "path",
        ["Grep"] = "path",
    };

    public static string? For(AgentConfig config, LocalStore store, string session, string toolName, JsonElement input)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        if (!PathField.TryGetValue(toolName, out var field)) return null;
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return Scopes.Unknown; // Glob or Grep without a path searches the session's folder: not a path a rule can be tied to
        }

        try
        {
            var os = AgentConfig.Os;
            var real = PathIdentity.Resolve(value.GetString(), os, allowMissing: true);
            if (real is null) return Scopes.Unknown;
            // The session's repository and every checkout the path itself lies in: a path in another repository, or in a submodule of one, is held to
            // those policies too.
            var (judged, around) = AccessRunKeys.KeysOf(real);
            if (!judged) return Scopes.Unknown;
            List<string> keys = [.. new[] { store.ProjectKeyOf(session) }.Concat(around).OfType<string>().Where(k => k.Length > 0).Distinct(RepoKeys.Comparer)];
            var saved = Identity.Peek(config);
            var policy = ExecPolicyLoader.Load(config, saved?.ExecLevel, saved?.ClaimedEnvironment, saved?.ClaimedFunction, saved?.ProjectFolders);
            // Only the folders are judged here: Claude's own tools are not the run's executable.
            List<AccessLayer> layers = [.. new AccessLayers(config).For(keys, policy).Select(l => l with { Applications = null })];
            return AccessGuard.Check(layers, new AccessCheck(real, [], null, null, false, false), os) switch
            {
                null => Scopes.Inside,
                { Code: AccessErrors.Unresolved } => Scopes.Unknown,
                _ => Scopes.Outside,
            };
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or NotSupportedException)
        {
            return Scopes.Unknown;
        }
    }
}
