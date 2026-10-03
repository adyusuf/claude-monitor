using System.Security.Cryptography;
using System.Text.Json;
using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.Auth;

/// <summary>
/// The agent's identity file (agent.json in the user-only home): which server it talks to, its agent and workspace
/// ids, and this installation's random machine key. No token is in it; tokens are in the OS credential store.
/// </summary>
public sealed record Identity(string MachineKey, string? Server = null, Guid? AgentId = null, Guid? WorkspaceId = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public bool Connected => Server is not null && AgentId is not null;

    public static Identity Load(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (File.Exists(config.IdentityPath))
        {
            var saved = JsonSerializer.Deserialize<Identity>(File.ReadAllText(config.IdentityPath), Json);
            if (saved is { MachineKey.Length: >= 16 }) return saved;
        }

        var fresh = new Identity(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)));
        fresh.Save(config);
        return fresh;
    }

    public void Save(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.EnsureHome();
        var temp = config.IdentityPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, config.IdentityPath, overwrite: true);
    }
}
