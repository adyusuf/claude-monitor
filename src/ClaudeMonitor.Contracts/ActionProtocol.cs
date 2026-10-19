namespace ClaudeMonitor.Contracts;

// Fixed agent actions asked for on the web (ADR-0005, "Agent actions from the web"). The request carries a kind from a closed
// set and nothing else; the agent performs it with its own code. Everything here only GROWS (global #4).

/// <summary>The closed set of actions. Anything else is refused by the API and ignored by an agent.</summary>
public static class AgentActionKinds
{
    public const string Restart = "restart";
    public const string UpdateCheck = "update_check";
    public const string Update = "update";
    public const string Status = "status";

    /// <summary>ADR-0014: start a Claude Code session in the background in a reported, trusted folder (`claude --bg`), and stop one the agent started (`claude stop`).</summary>
    public const string ClaudeStart = "claude_start";

    public const string ClaudeStop = "claude_stop";

    public static readonly IReadOnlyList<string> All = [Restart, UpdateCheck, Update, Status, ClaudeStart, ClaudeStop];

    /// <summary>Restart, update and a Claude start change the machine (or spend its owner's plan), so they ask for a recent sign-in (a fresh two-step code on a strict target).</summary>
    public static bool NeedsReauth(string kind) => kind is Restart or Update or ClaudeStart;

    public static bool IsClaude(string kind) => kind is ClaudeStart or ClaudeStop;

    /// <summary>The permission modes a remote start may ask for: every risky step waits for a person (manual), or plan, or file edits only. Never bypass, auto or dontAsk.</summary>
    public static readonly IReadOnlyList<string> ClaudePermissionModes = ["manual", "plan", "acceptEdits"];

    public const string DefaultClaudePermissionMode = "manual";

    /// <summary>The most characters of the optional first prompt of a Claude start (ADR-0014 P2). It is text from the web: capped, wrapped and labelled by the agent.</summary>
    public const int PromptMax = 2000;

    /// <summary>The environments an admin must have set on a machine before Claude may be started on it from the web (production and database machines never).</summary>
    public static readonly IReadOnlyList<string> ClaudeEnvironments = ["dev", "test"];

    /// <summary>The short id `claude --bg` prints: the first eight characters of the session id.</summary>
    public static bool IsSessionRef(string? text) => text is { Length: 8 } && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>A queued action nobody took in this long is expired: an offline machine does not restart "later" by surprise.</summary>
    public static readonly TimeSpan QueueTtl = TimeSpan.FromMinutes(10);

    /// <summary>The most characters of an agent's result text the API keeps.</summary>
    public const int ResultMax = 2000;
}

public static class AgentActionStatuses
{
    public const string Queued = "queued";
    public const string Delivered = "delivered";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Expired = "expired";

    public static readonly IReadOnlyList<string> All = [Queued, Delivered, Succeeded, Failed, Expired];

    public static readonly IReadOnlyList<string> Open = [Queued, Delivered];

    /// <summary>What an agent may report: it took the action, or it finished it.</summary>
    public static readonly IReadOnlyList<string> FromAgent = [Delivered, Succeeded, Failed];
}

/// <summary>An action for the agent to perform, sent on its stream (event <see cref="AgentStreamEvents.Action"/>).</summary>
public sealed record AgentActionMessage(Guid Id, string Kind, DateTimeOffset ExpiresAt, string? ProjectKey = null, string? PermissionMode = null, string? SessionRef = null, string? Prompt = null);

/// <summary>The agent's report: "delivered" when it takes the action, then "succeeded" or "failed" with a short text.</summary>
public sealed record AgentActionReport(string Status, string? Result = null, string? SessionRef = null);

/// <summary>The owner's request. Code: a two-step code, when the kind needs the re-authentication and the sign-in is not recent.</summary>
public sealed record AgentActionRequest(string Kind, string? Code = null, string? ProjectKey = null, string? PermissionMode = null, string? SessionRef = null, string? Prompt = null);

/// <summary>An action as the owner's page shows it. Result is plain text from the agent: data, never markup.</summary>
public sealed record AgentActionView(Guid Id, Guid AgentId, string Kind, string Status, string? Result, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? FinishedAt, string? ProjectKey = null, string? PermissionMode = null, string? SessionRef = null);

/// <summary>The machine owner's switch for starting Claude from the web on this machine (ADR-0014); off until they say so. Code: a two-step code when turning it on needs one.</summary>
public sealed record AgentClaudeRequest(bool Accept, string? Code = null, string? AutoMode = null);

/// <summary>The owner asks to start the test command the machine reports for a project (ADR-0011 §7a). The command is never part of the request.</summary>
public sealed record TestRunRequest(string ProjectKey);

public sealed record AgentClaudeView(Guid AgentId, bool Accepts, DateTimeOffset? SetAt, Guid? SetBy, string AutoMode = ClaudeAutoModes.Off);

/// <summary>
/// What the daemon does by itself with Claude on a machine (ADR-0014 P3), set by the machine's owner only. <c>on_message</c>: an ask from another machine that finds no idle session and names a project
/// this machine knows gets a background worker in that project. The agent only ever sees a mode the API found allowed (switch on, dev or test machine, not a service).
/// </summary>
public static class ClaudeAutoModes
{
    public const string Off = "off";
    public const string OnMessage = "on_message";
    public static readonly IReadOnlyList<string> All = [Off, OnMessage];
}

public static class AgentActionErrors
{
    public const string InvalidKind = "invalid_kind";

    /// <summary>Only the machine's owner (the agent's user) asks for an action on it.</summary>
    public const string OwnerOnly = "owner_only";

    // Claude control (ADR-0014): why a start or stop is refused. They are i18n keys the web translates.
    public const string ClaudeNotAccepted = "claude_control_not_accepted";
    public const string ClaudeEnvironment = "claude_control_environment";
    public const string ClaudeServiceMode = "claude_control_service_mode";
    public const string ClaudeNotInstalled = "claude_not_installed";
    public const string InvalidProject = "invalid_project";
    public const string InvalidPermissionMode = "invalid_permission_mode";
    public const string InvalidSessionRef = "invalid_session_ref";
    public const string InvalidPrompt = "invalid_prompt";
    public const string NoTestCommand = "no_test_command";
    public const string InvalidAutoMode = "invalid_auto_mode";
    public const string NotStartedHere = "claude_not_started_here";
}
