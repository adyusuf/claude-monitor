using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>
/// The agent's own log (agent.log in the user-only home), one line per entry, kept under a size cap by starting over.
/// It never holds captured content or tokens: only what the agent did and why something failed.
/// </summary>
public sealed class AgentLog(AgentConfig config, TimeProvider clock)
{
    public const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();

    public void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                config.EnsureHome();
                var info = new FileInfo(config.LogPath);
                if (info.Exists && info.Length > MaxBytes) File.Move(config.LogPath, config.LogPath + ".1", overwrite: true);
                File.AppendAllText(config.LogPath, $"{clock.GetUtcNow():O} [{Environment.ProcessId}] {line.ReplaceLineEndings(" ")}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // logging must never break a hook or the daemon
        }
        catch (UnauthorizedAccessException)
        {
            // as above
        }
    }
}
