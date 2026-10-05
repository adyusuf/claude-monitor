using ClaudeMonitor.Agent.Daemon;

namespace ClaudeMonitor.Agent.Config;

/// <summary>
/// Moves an agent from the older default home to the current one (Windows: %LOCALAPPDATA%\ClaudeMonitor to
/// %USERPROFILE%\.claude-monitor), so a machine that already logged in need not log in again: the tokens are in the OS
/// credential store, and only the identity file (agent.json: server, ids, machine key) is copied.
/// The local database (agent.db) is not moved: it holds only events not yet uploaded, and moving a SQLite file that a
/// running daemon may have open risks a torn copy; those few events are lost, which a monitor accepts.
/// A process started by a packaged app (the Claude desktop app) sees a redirected, usually empty, %LOCALAPPDATA%, so it
/// migrates from the old folder as ITS context sees it; the real old folder is migrated by the first process that runs
/// from a normal terminal. The old folder is never changed, and the identity file is never overwritten.
/// </summary>
public static class HomeMigration
{
    /// <summary>Copies agent.json once; never throws (a failed migration is logged and the agent goes on).</summary>
    public static bool Run(AgentConfig config, AgentLog log)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            if (config.MigrateFrom is not { Length: > 0 } from) return false;
            var source = Path.Combine(from, Path.GetFileName(config.IdentityPath));
            if (File.Exists(config.IdentityPath) || !File.Exists(source)) return false;
            config.EnsureHome();
            File.Copy(source, config.IdentityPath, overwrite: false);
            log.Write($"migrated agent.json from {from} to {config.Home}");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Write($"could not migrate agent.json from {config.MigrateFrom}: {e.GetType().Name} {e.Message}");
            return false;
        }
    }
}
