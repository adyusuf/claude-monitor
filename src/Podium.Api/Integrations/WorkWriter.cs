namespace Podium.Api.Integrations;

// The write side of a work tracker (ADR-0015 §7). Every call is made with the acting person's own account.

/// <summary>What changes on an issue. A field is sent only when its <c>Set…</c> flag is on, so "clear" and "leave" differ.</summary>
public sealed record ItemChange
{
    public string? Summary { get; init; }
    public bool SetDescription { get; init; }
    public string? Description { get; init; }
    public bool SetPriority { get; init; }
    public string? PriorityRank { get; init; }
    public bool SetEstimate { get; init; }
    public string? Estimate { get; init; }
    public bool SetAssignee { get; init; }
    public string? AssigneeAccountId { get; init; }
    public bool SetParent { get; init; }
    public string? ParentKey { get; init; }

    public bool IsEmpty => Summary is null && !SetDescription && !SetPriority && !SetEstimate && !SetAssignee && !SetParent;
}

public sealed record NewItem(string ContainerKey, string TypeName, ItemChange Fields);

/// <summary>A change to a sprint; null leaves a value as it is.</summary>
public sealed record IterationChange(string? Name, string? Goal, DateOnly? Start, DateOnly? End);

public interface IWorkWriter
{
    /// <summary>The tool's change value of an issue right now (to compare with what was last read).</summary>
    Task<string?> ItemStampAsync(TrackerContext ctx, string key, CancellationToken ct);

    Task UpdateItemAsync(TrackerContext ctx, string key, ItemChange change, CancellationToken ct);

    /// <summary>Moves the issue to the status that fits <paramref name="podiumStatus"/>; false when its workflow has no such step.</summary>
    Task<bool> TransitionItemAsync(TrackerContext ctx, string key, string podiumStatus, CancellationToken ct);

    /// <summary>Makes an issue; returns its tool id and key.</summary>
    Task<(string Id, string Key)> CreateItemAsync(TrackerContext ctx, NewItem item, CancellationToken ct);

    /// <summary>Ranks <paramref name="key"/> after another issue, or, with none given, before <paramref name="beforeKey"/>.</summary>
    Task RankItemAsync(TrackerContext ctx, string key, string? afterKey, string? beforeKey, CancellationToken ct);

    Task AddToIterationAsync(TrackerContext ctx, string iterationId, IReadOnlyList<string> keys, CancellationToken ct);

    Task MoveToBacklogAsync(TrackerContext ctx, IReadOnlyList<string> keys, CancellationToken ct);

    /// <summary>Makes a sprint on the container's first board; null when the container has none.</summary>
    Task<ExternalIteration?> CreateIterationAsync(TrackerContext ctx, string containerKey, IterationChange change, CancellationToken ct);

    Task UpdateIterationAsync(TrackerContext ctx, string iterationId, IterationChange change, CancellationToken ct);

    Task StartIterationAsync(TrackerContext ctx, string iterationId, DateOnly first, DateOnly last, CancellationToken ct);

    Task CloseIterationAsync(TrackerContext ctx, string iterationId, CancellationToken ct);

    Task DeleteIterationAsync(TrackerContext ctx, string iterationId, CancellationToken ct);
}
