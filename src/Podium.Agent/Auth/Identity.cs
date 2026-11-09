using System.Security.Cryptography;
using System.Text.Json;
using Podium.Agent.Config;
using Podium.Contracts;

namespace Podium.Agent.Auth;

/// <summary>
/// The agent's identity file (agent.json in the user-only home): which server it talks to, its agent and workspace
/// ids, this installation's random machine key and the settings the commands save (--stop-wait, --push, auto-update, and
/// --exec for an interactive agent; a service agent reads its exec level from an admin-owned file instead, ADR-0005), and
/// an interactive agent's access ceiling (`aipod config access`, ADR-0020 §4). No token is in it; tokens are in the OS credential store.
/// </summary>
public sealed record Identity(string MachineKey, string? Server = null, Guid? AgentId = null, Guid? WorkspaceId = null,
    int? StopWaitSeconds = null, bool? Push = null, string? AutoUpdate = null, string? ExecLevel = null, bool? ClaudeUpdate = null,
    string? PushTransport = null, string? ClaimedEnvironment = null, string? ClaimedFunction = null, List<string>? ProjectFolders = null, string? InboxFormat = null,
    Dictionary<string, List<string>>? ProjectTests = null, string? StatusLineBackup = null,
    List<string>? AccessAllowedRoots = null, List<string>? AccessDeniedRoots = null, List<AccessApp>? AccessExecutables = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The shortest machine key that counts; a shorter one (or none) is not an identity: "not connected" until login gives one.</summary>
    public const int MachineKeyMin = 16;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasMachineKey => MachineKey is { Length: >= MachineKeyMin };

    public bool Connected => HasMachineKey && Server is not null && AgentId is not null;

    /// <summary>
    /// The saved agent.json with its settings, or null when there is none or it is unreadable. Never creates or changes the file. One without a valid
    /// machine key is returned as it is (its settings count) and is never <see cref="Connected"/>.
    /// </summary>
    public static Identity? Peek(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        try
        {
            return File.Exists(config.IdentityPath) && JsonSerializer.Deserialize<Identity>(File.ReadAllText(config.IdentityPath), Json) is { } saved
                ? saved with { MachineKey = saved.MachineKey ?? "" }
                : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>No identity: what a status line or a tool reads as "not connected" when there is none. Never saved as it is.</summary>
    public static readonly Identity None = new("");

    /// <summary>
    /// The identity every path but login, connect and install reads (hooks, the daemon, MCP, status): the saved one, or, when this home has no
    /// agent.json yet, the one of an earlier home carried over once (<see cref="IdentityCarryOver"/>). Null when there is none; one without a valid
    /// machine key is never <see cref="Connected"/>. Never a new machine key, so a hook can never make the web see a second device.
    /// </summary>
    public static Identity? Find(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (Peek(config) is { } saved) return saved;
        return !File.Exists(config.IdentityPath) && IdentityCarryOver.TryCarry(config) ? Peek(config) : null;
    }

    /// <summary>The identity a settings command edits: the one <see cref="Find"/> gives (a machine key not set yet stays unset), else an empty one. It never mints one; login does.</summary>
    public static Identity ForEdit(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Find(config) ?? Raw(config) ?? None;
    }

    /// <summary>
    /// Login, connect and install only: the identity <see cref="Find"/> gives, else a new random machine key, keeping whatever settings agent.json
    /// already holds. A new file is created atomically under the identity lock, so a second process starting at the same moment reads the first one's.
    /// </summary>
    public static Identity Load(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (Find(config) is { HasMachineKey: true } found) return found;
        config.EnsureHome();
        using var gate = IdentityCarryOver.Lock(config);
        if (Peek(config) is { HasMachineKey: true } won) return won;
        var fresh = (Raw(config) ?? None) with { MachineKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)) };
        if (File.Exists(config.IdentityPath))
        {
            fresh.Save(config); // a file without a valid machine key: login replaces the key, never the settings
            return fresh;
        }

        return IdentityCarryOver.CreateNew(config.IdentityPath, JsonSerializer.Serialize(fresh, Json)) ? fresh : Peek(config) is { HasMachineKey: true } other ? other : fresh;
    }

    /// <summary>agent.json as it is, whatever its machine key; null when there is none. A file that cannot be read throws, as a settings command should.</summary>
    private static Identity? Raw(AgentConfig config) =>
        File.Exists(config.IdentityPath) ? JsonSerializer.Deserialize<Identity>(File.ReadAllText(config.IdentityPath), Json) : null;

    public void Save(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.EnsureHome();
        var temp = config.IdentityPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, config.IdentityPath, overwrite: true);
    }
}
