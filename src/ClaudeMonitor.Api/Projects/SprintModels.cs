namespace ClaudeMonitor.Api.Projects;

// The wire shapes of sprints (ADR-0011 §5). A PUT replaces the whole definition: a missing goal, name, date, capacity, release or team is
// cleared. ApplicationId is read on create only (a sprint keeps its scope for life).

public sealed record SprintInput(string? Name, string? Goal, DateOnly? StartDate, DateOnly? EndDate, decimal? Capacity, Guid? ApplicationId,
    Guid? ReleaseId, List<Guid>? TeamUserIds);

public sealed record SprintSummary(Guid Id, Guid ProjectId, Guid? ApplicationId, int Number, string? Name, string? Goal, DateOnly? StartDate,
    DateOnly? EndDate, decimal? Capacity, string Status, Guid? ReleaseId, string? ReleaseName, int IssueCount, decimal Committed, decimal Done,
    DateTimeOffset? StartedAt, DateTimeOffset? ClosedAt, DateTimeOffset CreatedAt, Guid? SourceConnectionId = null);

public sealed record SprintMemberView(Guid UserId, string DisplayName);

public sealed record SprintView(SprintSummary Sprint, IReadOnlyList<SprintMemberView> Team, IReadOnlyDictionary<string, int> StatusCounts, bool CanEdit);

public sealed record SprintIssuesInput(List<Guid>? IssueIds);

public sealed record CloseSprintInput(Guid? MoveUnfinishedTo);

public sealed record StatusInput(string? Status);
