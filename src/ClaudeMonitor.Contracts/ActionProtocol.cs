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

    /// <summary>
    /// The longest first prompt of a remote start (ADR-0014 P2), in characters. It is text from the web: capped here, wrapped and labelled by the agent, kept only until the
    /// agent has it, never written to an audit row or a log.
    /// </summary>
    public const int ClaudeFirstPromptMax = 2000;

    /// <summary>A first prompt is text a person typed: letters, spaces and new lines, no other control characters.</summary>
    public static bool IsFirstPrompt(string? text) =>
        text is { Length: >= 1 and <= ClaudeFirstPromptMax } && !string.IsNullOrWhiteSpace(text) && text.All(c => !char.IsControl(c) || c is '\n' or '\r' or '\t');

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
public sealed record AgentActionMessage(Guid Id, string Kind, DateTimeOffset ExpiresAt, string? ProjectKey = null, string? PermissionMode = null, string? SessionRef = null,
    string? FirstPrompt = null);

/// <summary>The agent's report: "delivered" when it takes the action, then "succeeded" or "failed" with a short text.</summary>
public sealed record AgentActionReport(string Status, string? Result = null, string? SessionRef = null);

/// <summary>The owner's request. Code: a two-step code, when the kind needs the re-authentication and the sign-in is not recent.</summary>
public sealed record AgentActionRequest(string Kind, string? Code = null, string? ProjectKey = null, string? PermissionMode = null, string? SessionRef = null,
    string? FirstPrompt = null);

/// <summary>An action as the owner's page shows it. Result is plain text from the agent: data, never markup.</summary>
public sealed record AgentActionView(Guid Id, Guid AgentId, string Kind, string Status, string? Result, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? FinishedAt, string? ProjectKey = null, string? PermissionMode = null, string? SessionRef = null);

/// <summary>The machine owner's switch for starting Claude from the web on this machine (ADR-0014); off until they say so. Code: a two-step code when turning it on needs one.</summary>
public sealed record AgentClaudeRequest(bool Accept, string? Code = null);

public sealed record AgentClaudeView(Guid AgentId, bool Accepts, DateTimeOffset? SetAt, Guid? SetBy);

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
    public const string NotStartedHere = "claude_not_started_here";
    public const string InvalidFirstPrompt = "invalid_first_prompt";

    /// <summary>Another start of this machine is still open (queued or running its start): wait for it, then ask again.</summary>
    public const string ClaudeStartPending = "claude_start_pending";

    /// <summary>The machine's agent read Claude Code's own trust record and the folder is not in it: a start would only be refused there (ADR-0014 P2).</summary>
    public const string FolderNotTrusted = "claude_folder_not_trusted";
}
