using ClaudeMonitor.Api.Data;

namespace ClaudeMonitor.Api.Projects;

// The wire shapes of issues (ADR-0011 §4). A PUT replaces the whole issue: a missing optional field is cleared; a missing type, priority,
// status or estimate keeps its current value (a new issue takes the project's first item type, the middle of its scale and "backlog").

public sealed record CriterionEntry(string? Given, string? When, string? Then);

public sealed record IssueInput(string? Title, string? Type, string? Priority, string? Estimate, Guid? AssigneeUserId, Guid? ParentId,
    Guid? ApplicationId, Guid? ComponentId, Guid? ReleaseId, string? Status, string? Description, List<CriterionEntry>? AcceptanceCriteria,
    string? TechnicalDesign, string? BugEnvironment, string? ReproSteps, string? ExpectedResult, string? ActualResult, string? Severity);

public sealed record IssueSummary(Guid Id, Guid ProjectId, Guid? ApplicationId, int Number, string Title, string Type, string? Priority,
    string? Estimate, string Status, Guid? AssigneeUserId, string? AssigneeName, Guid? ParentId, Guid? ComponentId, Guid? ReleaseId,
    double Position, bool Archived, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? SprintId = null);

public sealed record IssueLinkView(Guid LinkId, Guid IssueId, int Number, string Title, string Status);

public sealed record IssueView(IssueSummary Issue, string? Description, IReadOnlyList<CriterionEntry> AcceptanceCriteria, string? TechnicalDesign,
    string? BugEnvironment, string? ReproSteps, string? ExpectedResult, string? ActualResult, string? Severity, string? ParentTitle,
    IReadOnlyList<IssueLinkView> Blocks, IReadOnlyList<IssueLinkView> BlockedBy, bool CanEdit);

/// <summary>Moves an issue in the backlog: after the given issue, or to the top when null. Both ends are issues of the same project.</summary>
public sealed record PositionInput(Guid? AfterIssueId);

public sealed record LinkInput(Guid? OtherIssueId, string? Kind);

public sealed record TestCaseInput(string? Title, string? Steps, string? Expected);

public sealed record TestResultInput(string? Result, string? Note);

public sealed record TestCaseView(Guid Id, Guid IssueId, string Title, string? Steps, string? Expected, string Result, string? ResultNote,
    Guid? ResultBy, string? ResultByName, DateTimeOffset? ResultAt, DateTimeOffset CreatedAt);

public sealed record CodeLinkInput(string? Kind, string? Url, string? Title);

public sealed record CodeLinkView(Guid Id, Guid IssueId, string Kind, string Url, string? Title, string Source, DateTimeOffset CreatedAt);

public sealed record IssueRunInput(Guid? RunId);

/// <summary>A remote run linked to an issue: what ran where and how it ended, never its command or output.</summary>
public sealed record IssueRunView(Guid RunId, string? Kind, string Status, int? ExitCode, int? TestsPassed, int? TestsFailed, int? TestsSkipped,
    string TargetHostname, string? ProjectKey, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt, DateTimeOffset LinkedAt);

/// <summary>An issue a run is linked to, seen from the run.</summary>
public sealed record RunIssueView(Guid IssueId, Guid ProjectId, int Number, string Title, string Status);
