namespace ClaudeMonitor.Api.Data;

// Issues and what hangs on them (ADR-0011 §4, docs/data-model.md §7).

public static class IssueStatuses
{
    public const string Backlog = "backlog";
    public const string Ready = "ready";
    public const string InProgress = "in_progress";
    public const string InReview = "in_review";
    public const string Done = "done";
    public const string Cancelled = "cancelled";
    public static readonly IReadOnlyList<string> All = [Backlog, Ready, InProgress, InReview, Done, Cancelled];
}

public static class BugSeverities
{
    public const string Critical = "critical";
    public const string Major = "major";
    public const string Minor = "minor";
    public const string Trivial = "trivial";
    public static readonly IReadOnlyList<string> All = [Critical, Major, Minor, Trivial];
}

public static class IssueLinkKinds
{
    public const string Blocks = "blocks";
    public const string BlockedBy = "blocked_by";
    public static readonly IReadOnlyList<string> All = [Blocks, BlockedBy];
}

public static class TestResults
{
    public const string NotRun = "not_run";
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Blocked = "blocked";
    public static readonly IReadOnlyList<string> All = [NotRun, Passed, Failed, Blocked];
}

public static class CodeLinkKinds
{
    public const string PullRequest = "pr";
    public const string Commit = "commit";
    public static readonly IReadOnlyList<string> All = [PullRequest, Commit];
}

public static class CodeLinkSources
{
    public const string Manual = "manual";
    public const string Auto = "auto";
    public static readonly IReadOnlyList<string> All = [Manual, Auto];
}

public sealed class Issue
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? ApplicationId { get; set; }
    /// <summary>The issue's number inside its project (#12); never reused.</summary>
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string TitleSearch { get; set; } = "";
    public string Type { get; set; } = WorkItemTypes.Task;
    public string? Priority { get; set; }
    public string? Estimate { get; set; }
    public string Status { get; set; } = IssueStatuses.Backlog;
    public Guid? AssigneeUserId { get; set; }
    public Guid? ParentId { get; set; }
    public Guid? ComponentId { get; set; }
    public Guid? ReleaseId { get; set; }
    /// <summary>The sprint the issue is planned into; null while it is in the backlog.</summary>
    public Guid? SprintId { get; set; }
    public string? Description { get; set; }
    /// <summary>jsonb: <c>[{given, when, then}]</c>, at most 30.</summary>
    public string? AcceptanceCriteria { get; set; }
    public string? TechnicalDesign { get; set; }
    public string? BugEnvironment { get; set; }
    public string? ReproSteps { get; set; }
    public string? ExpectedResult { get; set; }
    public string? ActualResult { get; set; }
    public string? Severity { get; set; }
    /// <summary>The order in the backlog, ascending; a move writes the middle of its two neighbours.</summary>
    public double Position { get; set; }
    /// <summary>The tool's own key and address when the issue mirrors one (ADR-0015), such as ABC-12.</summary>
    public string? ExternalKey { get; set; }
    public string? ExternalUrl { get; set; }
    /// <summary>The tool's person it is assigned to, who need not be a Podium member.</summary>
    public Guid? AssigneePersonId { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One "blocks" edge: <see cref="BlockerIssueId"/> blocks <see cref="BlockedIssueId"/>. "Blocked by" is the same row seen from the other end.</summary>
public sealed class IssueLink
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid BlockerIssueId { get; set; }
    public Guid BlockedIssueId { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class IssueTestCase
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid IssueId { get; set; }
    public string Title { get; set; } = "";
    public string? Steps { get; set; }
    public string? Expected { get; set; }
    public string Result { get; set; } = TestResults.NotRun;
    public string? ResultNote { get; set; }
    public Guid? ResultBy { get; set; }
    public DateTimeOffset? ResultAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class IssueCodeLink
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid IssueId { get; set; }
    public string Kind { get; set; } = CodeLinkKinds.PullRequest;
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string Source { get; set; } = CodeLinkSources.Manual;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A remote run (a test, a build) the team attached to an issue, so its result follows the work.</summary>
public sealed class IssueRun
{
    public Guid IssueId { get; set; }
    public Guid RunId { get; set; }
    public Guid LinkedBy { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
}
