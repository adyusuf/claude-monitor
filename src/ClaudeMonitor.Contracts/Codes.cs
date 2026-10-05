namespace ClaudeMonitor.Contracts;

// The closed sets the API, the agent and the database share (global #11). The database holds the same
// strings behind a CHECK; code that switches on them always has a default branch.

public static class HarnessKinds
{
    public const string ClaudeCode = "claude_code";
}

public static class EventKinds
{
    /// <summary>A hook payload; the full kind is this prefix plus the hook event name, e.g. "hook:Stop".</summary>
    public const string HookPrefix = "hook:";

    /// <summary>One new line of the session transcript.</summary>
    public const string Transcript = "transcript";

    /// <summary>The token usage of one assistant message, counted once per message id by the agent.</summary>
    public const string Usage = "usage";

    /// <summary>A note the model reported through the agent's MCP tool.</summary>
    public const string Note = "note";

    public static string Hook(string hookEvent) => HookPrefix + hookEvent;
}

public static class CommandKinds
{
    public const string Prompt = "prompt";
    public const string Stop = "stop";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Prompt, Stop };
}

public static class CommandStatuses
{
    public const string Queued = "queued";
    public const string Delivered = "delivered";
    public const string Applied = "applied";
    public const string Failed = "failed";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";

    /// <summary>What an agent may report back.</summary>
    public static readonly IReadOnlySet<string> FromAgent = new HashSet<string> { Delivered, Applied, Failed };
}

/// <summary>The harness tools whose permission prompt is a question for the person, answered with a choice.</summary>
public static class QuestionTools
{
    public const string AskUserQuestion = "AskUserQuestion";
}

public static class PermissionDecisions
{
    public const string Allow = "allow";
    public const string Deny = "deny";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Allow, Deny };
}

public static class OsKinds
{
    public const string MacOs = "macos";
    public const string Windows = "windows";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { MacOs, Windows };
}
