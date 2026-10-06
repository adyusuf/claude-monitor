using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;

namespace ClaudeMonitor.Agent.ClaudeUpdate;

/// <summary>Updating Claude Code needs BOTH this machine's consent and the workspace's. Unread or unknown is no.</summary>
public static class ClaudePolicy
{
    /// <summary>The workspace's answer, as the daemon last read it from the API ("true" / "false").</summary>
    public const string WorkspaceKey = "settings.claude_update";

    public static bool Allowed(AgentConfig config, LocalStore store)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        return SavedSettings.Apply(config).ClaudeUpdateEnabled && store.Get(WorkspaceKey) == "true"; // the saved setting is read afresh: this daemon may be old
    }
}

/// <summary>Tells the person something. Best effort: where there is no way to, it says so and the countdown still runs (log and `cm-agent status` carry it).</summary>
public interface IUserNotifier
{
    bool Notify(string message);
}

public sealed class SystemNotifier(IProcessRunner runner, TimeSpan timeout) : IUserNotifier
{
    /// <summary>macOS shows a notification; no other platform has a notifier here (a Windows toast was not built or tested).</summary>
    public bool Notify(string message)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        // the message is built by this agent from numbers and fixed text, never from anything a session or the server sent
        var safe = new string(message.Where(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or ',' or ':' or '-' or '(' or ')' or '`').ToArray());
        return runner.Run("/usr/bin/osascript", ["-e", $"display notification \"{safe}\" with title \"Claude Monitor\""], timeout).ExitCode == 0;
    }
}
