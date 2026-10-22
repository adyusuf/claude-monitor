namespace Podium.Contracts;

// Asks across machines (ADR-0008 §3f): a Claude session on one machine writes a question, the daemons carry it to an idle Claude session on another
// machine of the same user, and the answer comes back through aipod_answer. A daemon never originates an ask. Closed sets and wire shapes only.

/// <summary>The life of an ask. Every transition is a compare-and-set on the status. An ask waits as <c>queued</c> until a session takes it or its time runs out.</summary>
public static class AskStatuses
{
    public const string Queued = "queued";
    public const string Delivered = "delivered";
    public const string Answered = "answered";
    public const string NoIdleSession = "no_idle_session";
    public const string NotAccepting = "not_accepting";
    public const string NoAnswer = "no_answer";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";

    /// <summary>
    /// A report, not a state: the target's daemon has the ask (it is kept there until a Claude session takes it). The API stores the time as
    /// <c>ReceivedAt</c> and the status stays <c>queued</c>, so it is in neither <see cref="All"/> nor <see cref="Open"/> nor <see cref="Final"/>.
    /// </summary>
    public const string Received = "received";

    public static readonly IReadOnlyList<string> All = [Queued, Delivered, Answered, NoIdleSession, NotAccepting, NoAnswer, Expired, Cancelled];
    public static readonly IReadOnlySet<string> Open = new HashSet<string> { Queued, Delivered };
    public static readonly IReadOnlySet<string> Final = new HashSet<string> { Answered, NoIdleSession, NotAccepting, NoAnswer, Expired, Cancelled };

    /// <summary>What the target's agent may report about an ask it received.</summary>
    public static readonly IReadOnlySet<string> FromTarget = new HashSet<string> { Received, Delivered, NoIdleSession, NotAccepting };
}

/// <summary>Why an ask was refused at creation or a later step; the codes are i18n keys.</summary>
public static class AskErrors
{
    public const string NoClaude = "no_claude";
    public const string NotAccepted = "asks_not_accepted";
    public const string OtherUser = "ask_other_user";
    public const string OtherWorkspace = "ask_other_workspace";
    public const string Depth = "ask_depth";
    public const string Loop = "ask_loop";
    public const string Rate = "ask_rate";
    public const string NotOpen = "ask_not_open";
    public const string Mismatch = "ask_mismatch";
    public const string InvalidQuestion = "invalid_question";
    public const string InvalidAnswer = "invalid_answer";
}

/// <summary>A question from a Claude session. ClientKey makes a retry return the same ask; SessionExternalId names the asking session for the rate cap.</summary>
public sealed record AskCreate(string ClientKey, Guid TargetAgentId, string Question, string? SessionExternalId = null, string? ProjectKey = null);

public sealed record AskCreated(Guid Id, string Status, DateTimeOffset ExpiresAt);

/// <summary>An ask as its requester (and the target's owner) reads it; the question and the answer are untrusted text and are returned only to them.</summary>
public sealed record AskView(Guid Id, Guid TargetAgentId, string TargetHostname, string Status, string? Question, string? Answer, bool AnswerTruncated,
    string? Error, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? DeliveredAt, DateTimeOffset? AnsweredAt, DateTimeOffset? ReceivedAt = null);

/// <summary>The ask as the target's daemon receives it. FromLabel is the requesting machine's label, never free text.</summary>
public sealed record AskMessage(Guid Id, string Question, string FromLabel, string? ProjectKey, DateTimeOffset NotAfter);

/// <summary>What the target's daemon reports: received (it holds the ask), delivered (with the session it was written to), or why it was not (no idle session, not accepting).</summary>
public sealed record AskStatusUpdate(string Status, string? Error = null, string? SessionExternalId = null, DateTimeOffset? At = null);

/// <summary>The answer, once, from the session that received the ask.</summary>
public sealed record AskAnswer(string Text, string? SessionExternalId = null);

public sealed record AskCancelMessage(Guid Id);

/// <summary>Sent to the requester's agent when its ask changes, so it fetches the ask without waiting.</summary>
public sealed record AskUpdateMessage(Guid Id, string Status);
