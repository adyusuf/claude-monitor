namespace ClaudeMonitor.Api.Data;

// Managed projects and their applications (ADR-0011, docs/data-model.md §7).

public static class ManagedStatuses
{
    public const string Active = "active";
    public const string Archived = "archived";
    public static readonly IReadOnlyList<string> All = [Active, Archived];
}

public static class WorkItemTypes
{
    public const string Epic = "epic";
    public const string Story = "story";
    public const string Task = "task";
    public const string Bug = "bug";
    public const string Spike = "spike";
    public static readonly IReadOnlyList<string> All = [Epic, Story, Task, Bug, Spike];
}

public static class PriorityScales
{
    public const string P0ToP3 = "p0_p3";
    public const string MoSCoW = "moscow";
    public const string HighMediumLow = "high_medium_low";
    public const string OneToFive = "one_to_five";
    public static readonly IReadOnlyList<string> All = [P0ToP3, MoSCoW, HighMediumLow, OneToFive];
}

public static class EstimationUnits
{
    public const string StoryPoints = "story_points";
    public const string Hours = "hours";
    public const string Days = "days";
    public const string TShirt = "t_shirt";
    public static readonly IReadOnlyList<string> All = [StoryPoints, Hours, Days, TShirt];
}

public static class ProjectRoles
{
    public const string Manager = "manager";
    public const string Contributor = "contributor";
    public const string Reviewer = "reviewer";
    public const string Viewer = "viewer";
    public static readonly IReadOnlyList<string> All = [Manager, Contributor, Reviewer, Viewer];
}

public static class ApplicationKinds
{
    public const string Web = "web";
    public const string Mobile = "mobile";
    public const string Api = "api";
    public const string Other = "other";
    public static readonly IReadOnlyList<string> All = [Web, Mobile, Api, Other];
}

public static class EnvironmentNames
{
    public const string Dev = "dev";
    public const string Test = "test";
    public const string Prod = "prod";
    public static readonly IReadOnlyList<string> All = [Dev, Test, Prod];
}

public static class VersioningSchemes
{
    public const string SemVer = "semver";
    public const string CalVer = "calver";
    public const string None = "none";
    public static readonly IReadOnlyList<string> All = [SemVer, CalVer, None];
}

public static class TargetKinds
{
    public const string Performance = "performance";
    public const string Accessibility = "accessibility";
    public const string Sla = "sla";
    public const string Security = "security";
    public const string Other = "other";
    public static readonly IReadOnlyList<string> All = [Performance, Accessibility, Sla, Security, Other];
}

public static class ComponentKinds
{
    public const string Component = "component";
    public const string Module = "module";
    public static readonly IReadOnlyList<string> All = [Component, Module];
}

public static class ReleaseStatuses
{
    public const string Planned = "planned";
    public const string Released = "released";
    public const string Cancelled = "cancelled";
    public static readonly IReadOnlyList<string> All = [Planned, Released, Cancelled];
}

/// <summary>A managed project (ADR-0011). Not the same as <see cref="Repo"/>, the work folder a session reports.</summary>
public sealed class ManagedProject
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string NameSearch { get; set; } = "";
    public string? Purpose { get; set; }
    public Guid OwnerUserId { get; set; }
    public string? Sponsor { get; set; }
    /// <summary>jsonb: <c>[{name, role}]</c>, at most 50.</summary>
    public string? Stakeholders { get; set; }
    public decimal? BudgetAmount { get; set; }
    public string? BudgetCurrency { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string[] ItemTypes { get; set; } = [.. WorkItemTypes.All];
    public string PriorityScale { get; set; } = PriorityScales.P0ToP3;
    public string EstimationUnit { get; set; } = EstimationUnits.StoryPoints;
    public int SprintLengthDays { get; set; } = 14;
    public string[] DefinitionOfReady { get; set; } = [];
    public string[] DefinitionOfDone { get; set; } = [];
    public string Status { get; set; } = ManagedStatuses.Active;
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ProjectMember
{
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = ProjectRoles.Viewer;
    public Guid AddedBy { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

public sealed class ManagedApplication
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string NameSearch { get; set; } = "";
    public string? Description { get; set; }
    public string Kind { get; set; } = ApplicationKinds.Web;
    public string? RepoUrl { get; set; }
    /// <summary>jsonb: <c>[{name, url}]</c>, names from <see cref="EnvironmentNames"/>, at most 3.</summary>
    public string? Environments { get; set; }
    public string[] TechStack { get; set; } = [];
    public string VersioningScheme { get; set; } = VersioningSchemes.SemVer;
    public Guid? TechOwnerUserId { get; set; }
    /// <summary>jsonb: <c>[{kind, target}]</c>, at most 20.</summary>
    public string? NonFunctionalTargets { get; set; }
    public string Status { get; set; } = ManagedStatuses.Active;
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ApplicationComponent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid ApplicationId { get; set; }
    public string Name { get; set; } = "";
    public string NameSearch { get; set; } = "";
    public string Kind { get; set; } = ComponentKinds.Component;
    public string Status { get; set; } = ManagedStatuses.Active;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ApplicationRelease
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public Guid ApplicationId { get; set; }
    public string Name { get; set; } = "";
    public string NameSearch { get; set; } = "";
    public DateOnly? PlannedDate { get; set; }
    public string Status { get; set; } = ReleaseStatuses.Planned;
    public DateTimeOffset CreatedAt { get; set; }
}
