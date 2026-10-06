using System.Text.Json;
using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.Update;

/// <summary>Why an update did or did not happen, in the stable words `cm-agent status` and agent.log use.</summary>
public static class UpdateCodes
{
    public const string UpToDate = "up-to-date";
    public const string Available = "available";
    public const string Installed = "installed";
    public const string RolledBack = "rolled-back";
    public const string NoKey = "no-key";
    public const string Channel = "channel";
    public const string BadSignature = "bad-signature";
    public const string Malformed = "malformed";
    public const string Downgrade = "downgrade";
    public const string Unsupported = "unsupported-platform";
    public const string BadUrl = "bad-url";
    public const string TooLarge = "too-large";
    public const string HashMismatch = "hash-mismatch";
    public const string BadArchive = "bad-archive";
    public const string BadBinary = "bad-binary";
    public const string NotInstalled = "not-installed";
    public const string Busy = "busy";
    public const string Unreachable = "unreachable";
    public const string Failed = "failed";
    public const string RollbackStuck = "rollback-stuck";
    public const string Interrupted = "interrupted";
}

/// <summary>
/// What the updater remembers between runs (update-state.json in the home): the last check and its verdict, a build
/// that was rolled back (never retried by itself), and the install in progress. Read by `cm-agent status`.
/// </summary>
public sealed record UpdateState(string? CheckedAt = null, string? Result = null, string? Detail = null, string? Available = null,
    string? Phase = null, string? From = null, string? To = null, string? AttemptedAt = null, string? BlockedVersion = null,
    string? InstalledAt = null)
{
    public const string PendingHealth = "pending-health";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static UpdateState Load(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        try
        {
            return File.Exists(config.UpdateStatePath) ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(config.UpdateStatePath), Json) ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>Reads the saved state, changes it, saves it.</summary>
    public static void Change(AgentConfig config, Func<UpdateState, UpdateState> change) => change(Load(config)).Save(config);

    public void Save(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.EnsureHome();
        // Best effort and never a crash: two writers (the daemon's check and the updater) may meet; a unique temp name keeps them apart.
        var temp = config.UpdateStatePath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, config.UpdateStatePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception d) when (d is IOException or UnauthorizedAccessException)
            {
                // nothing more to do
            }
        }
    }
}
