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

    public static readonly IReadOnlyList<string> All = [Restart, UpdateCheck, Update, Status];

    /// <summary>Restart and update change the machine, so they ask for a recent sign-in (a fresh two-step code on a strict target).</summary>
    public static bool NeedsReauth(string kind) => kind is Restart or Update;

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
public sealed record AgentActionMessage(Guid Id, string Kind, DateTimeOffset ExpiresAt);

/// <summary>The agent's report: "delivered" when it takes the action, then "succeeded" or "failed" with a short text.</summary>
public sealed record AgentActionReport(string Status, string? Result = null);

/// <summary>The owner's request. Code: a two-step code, when the kind needs the re-authentication and the sign-in is not recent.</summary>
public sealed record AgentActionRequest(string Kind, string? Code = null);

/// <summary>An action as the owner's page shows it. Result is plain text from the agent: data, never markup.</summary>
public sealed record AgentActionView(Guid Id, Guid AgentId, string Kind, string Status, string? Result, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? FinishedAt);

public static class AgentActionErrors
{
    public const string InvalidKind = "invalid_kind";

    /// <summary>Only the machine's owner (the agent's user) asks for an action on it.</summary>
    public const string OwnerOnly = "owner_only";
}
