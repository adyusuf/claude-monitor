namespace ClaudeMonitor.Api.Integrations;

// The one view of a project tool that the import and sync code knows (ADR-0015 §2). A new tool is one new IWorkTracker.

/// <summary>A tool a connection can be made to. A closed set stored as text (global #11).</summary>
public static class Providers
{
    public const string Jira = "jira";
    public static readonly IReadOnlyList<string> All = [Jira];
}

/// <summary>A person in the tool. The e-mail is often hidden by the tool; null is normal.</summary>
public sealed record ExternalPerson(string Id, string DisplayName, string? Email, bool Active);

/// <summary>A space of the tool (a Jira project): the thing that becomes a Podium project.</summary>
public sealed record ExternalContainer(string Id, string Key, string Name, string? Description, ExternalPerson? Lead, string? Url);

/// <summary>A sprint. <see cref="State"/> is the tool's word, mapped by the importer.</summary>
public sealed record ExternalIteration(string Id, string Name, string State, string? Goal, DateOnly? Start, DateOnly? End, string ContainerKey);

/// <summary>An issue. Every text is untrusted; the importer caps and cleans it.</summary>
public sealed record ExternalItem(
    string Id,
    string Key,
    string ContainerKey,
    string Summary,
    string? Description,
    string TypeName,
    bool IsSubtask,
    string? ParentKey,
    string? ParentTypeName,
    string? PriorityName,
    string StatusName,
    string StatusCategory,
    string? Resolution,
    string? Estimate,
    ExternalPerson? Assignee,
    ExternalPerson? Reporter,
    ExternalIteration? Iteration,
    string UpdatedStamp);

/// <summary>What a tool call needs: the site and a way to get a valid access token. The tracker never stores a token.</summary>
public sealed record TrackerContext(string SiteId, Func<CancellationToken, Task<string>> AccessToken);

/// <summary>A failure with a short code the web can translate; never carries the tool's response text.</summary>
public sealed class TrackerException(string code) : Exception(code)
{
    public string Code { get; } = code;

    public static class Codes
    {
        public const string Unauthorized = "tracker_unauthorized";
        public const string Forbidden = "tracker_forbidden";
        public const string NotFound = "tracker_not_found";
        public const string RateLimited = "tracker_rate_limited";
        public const string Unavailable = "tracker_unavailable";
        public const string Rejected = "tracker_rejected";
        public const string Changed = "tracker_changed";
    }
}

public interface IWorkTracker
{
    string Provider { get; }

    Task<IReadOnlyList<ExternalContainer>> ListContainersAsync(TrackerContext ctx, CancellationToken ct);

    Task<ExternalContainer?> GetContainerAsync(TrackerContext ctx, string key, CancellationToken ct);

    /// <summary>Every sprint of the container's boards, each once.</summary>
    Task<IReadOnlyList<ExternalIteration>> ListIterationsAsync(TrackerContext ctx, string containerKey, CancellationToken ct);

    /// <summary>The issues in no open sprint, in the tool's own order.</summary>
    Task<IReadOnlyList<ExternalItem>> ListBacklogAsync(TrackerContext ctx, string containerKey, CancellationToken ct);

    Task<IReadOnlyList<ExternalItem>> ListIterationItemsAsync(TrackerContext ctx, string iterationId, CancellationToken ct);

    Task<ExternalItem?> GetItemAsync(TrackerContext ctx, string key, CancellationToken ct);

    Task<ExternalIteration?> GetIterationAsync(TrackerContext ctx, string iterationId, CancellationToken ct);

    /// <summary>Issues of these containers changed at or after <paramref name="since"/> (the reconciliation pass).</summary>
    Task<IReadOnlyList<ExternalItem>> ListChangedAsync(TrackerContext ctx, IReadOnlyList<string> containerKeys, DateTimeOffset since,
        CancellationToken ct);

    Task<IReadOnlyList<ExternalPerson>> ListPeopleAsync(TrackerContext ctx, string containerKey, CancellationToken ct);
}
