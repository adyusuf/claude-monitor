using System.Text.Json;
using Podium.Agent.Config;
using Podium.Agent.Daemon;

namespace Podium.Agent.Auth;

/// <summary>
/// The one-time carry-over of an earlier home's identity (ADR-0016 §4), as the installer scripts do it, for an agent that was updated by its binary
/// alone: when this home has no agent.json and an earlier home of this OS (<see cref="AgentConfig.LegacyHomes"/>) has one with a valid machine key, its
/// two tokens are copied into this agent's credential store (one already there is never overwritten) and then agent.json itself is copied. The earlier
/// home and its credential entries are left as they are. The identity lock and an atomic create keep a hook and the daemon starting together from both
/// writing, and from minting a second machine key.
/// </summary>
public static class IdentityCarryOver
{
    private const string LockName = "identity.lock";
    private const int LockTries = 40;
    private static readonly TimeSpan LockPause = TimeSpan.FromMilliseconds(50);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>True when this home has an agent.json now (carried over, or made by another process meanwhile); false when no earlier home has one.</summary>
    public static bool TryCarry(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        foreach (var legacy in config.LegacyHomes)
        {
            if (Readable(Path.Combine(legacy, "agent.json")) is not { } text) continue;
            config.EnsureHome();
            using var gate = Lock(config);
            if (File.Exists(config.IdentityPath)) return true;
            CarryTokens(config, legacy);
            CreateNew(config.IdentityPath, text);
            new AgentLog(config, TimeProvider.System).Write("the identity of the previous home was carried over (same machine key, same login)");
            return true;
        }

        return false;
    }

    /// <summary>
    /// The identity lock of this home, held while an identity file is created; null when another process keeps it longer than about two seconds (the
    /// atomic create still lets only one file win).
    /// </summary>
    public static FileStream? Lock(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        for (var i = 0; i < LockTries; i++)
        {
            try
            {
                return new FileStream(Path.Combine(config.Home, LockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(LockPause); // another process creates the identity right now
            }
        }

        return null;
    }

    /// <summary>Writes the file only if it does not exist yet, through a temp file of its own and a rename that never replaces; false when another won.</summary>
    public static bool CreateNew(string path, string text)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, text);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temp); // another process created it first: its file stands
            return false;
        }
    }

    /// <summary>The text of an earlier agent.json that holds a valid machine key and is not a link; null otherwise.</summary>
    private static string? Readable(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).LinkTarget is not null) return null;
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Identity>(text, Json) is { MachineKey.Length: >= 16 } ? text : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // an earlier home that cannot be read has nothing to carry over
        }
    }

    /// <summary>Copies the two tokens; a store that refuses is logged by the exception's type only, and the login is asked for again later.</summary>
    private static void CarryTokens(AgentConfig config, string legacyHome)
    {
        var target = Credentials.For(config);
        var source = Credentials.Legacy(config, legacyHome);
        foreach (var account in new[] { Credentials.Access, Credentials.Refresh })
        {
            try
            {
                if (target.Read(account) is not null || source.Read(account) is not { Length: > 0 } secret) continue;
                target.Write(account, secret);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                or System.ComponentModel.Win32Exception)
            {
                new AgentLog(config, TimeProvider.System).Write($"the {account} of the previous home was not carried over ({e.GetType().Name})");
            }
        }
    }
}
