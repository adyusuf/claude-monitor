namespace Podium.Api.Data;

// Sprints and their teams (ADR-0011 §5, docs/data-model.md §7).

public static class SprintStatuses
{
    public const string Planned = "planned";
    public const string Active = "active";
    public const string Closed = "closed";
    public static readonly IReadOnlyList<string> All = [Planned, Active, Closed];
}

public sealed class Sprint
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    /// <summary>The application the sprint was started for; null for a project-wide sprint. Never changes.</summary>
    public Guid? ApplicationId { get; set; }
    /// <summary>The sprint's number inside its project; never reused.</summary>
    public int Number { get; set; }
    public string? Name { get; set; }
    public string? Goal { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    /// <summary>In the project's estimation unit.</summary>
    public decimal? Capacity { get; set; }
    public string Status { get; set; } = SprintStatuses.Planned;
    public Guid? ReleaseId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    /// <summary>The work tracker connection this sprint mirrors (ADR-0015); null for a sprint made in Podium.</summary>
    public Guid? SourceConnectionId { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SprintMember
{
    public Guid SprintId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}
