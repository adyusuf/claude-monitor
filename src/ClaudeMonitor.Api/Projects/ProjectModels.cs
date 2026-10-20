using ClaudeMonitor.Api.Data;

namespace ClaudeMonitor.Api.Projects;

// The wire shapes of managed projects and applications (ADR-0011). A PUT replaces the whole definition: an optional text, a date, the
// budget and a list that is missing are cleared; the four settings with a default (item types, priority scale, estimation unit and
// sprint length) keep their current value when missing.

public sealed record Stakeholder(string? Name, string? Role);

public sealed record ProjectInput(string? Name, string? Purpose, Guid? OwnerUserId, string? Sponsor, List<Stakeholder>? Stakeholders,
    decimal? BudgetAmount, string? BudgetCurrency, DateOnly? StartDate, DateOnly? EndDate, List<string>? ItemTypes, string? PriorityScale,
    string? EstimationUnit, int? SprintLengthDays, List<string>? DefinitionOfReady, List<string>? DefinitionOfDone);

public sealed record ProjectAccessView(bool CanEdit, bool CanArchive, bool CanEditApplications);

public sealed record ProjectSummary(Guid Id, string Name, string? Purpose, Guid OwnerUserId, string? OwnerName, DateOnly? StartDate,
    DateOnly? EndDate, string Status, int ApplicationCount, DateTimeOffset CreatedAt, Guid? SourceConnectionId = null);

public sealed record ProjectView(Guid Id, Guid WorkspaceId, string Name, string? Purpose, Guid OwnerUserId, string? OwnerName, string? Sponsor,
    IReadOnlyList<Stakeholder> Stakeholders, decimal? BudgetAmount, string? BudgetCurrency, DateOnly? StartDate, DateOnly? EndDate,
    IReadOnlyList<string> ItemTypes, string PriorityScale, string EstimationUnit, int SprintLengthDays,
    IReadOnlyList<string> DefinitionOfReady, IReadOnlyList<string> DefinitionOfDone, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, ProjectAccessView Access, Guid? SourceConnectionId = null);

public sealed record ProjectMemberView(Guid UserId, string DisplayName, string? Email, string Role, DateTimeOffset AddedAt);

public sealed record ProjectMemberInput(string? Role);

public sealed record EnvironmentEntry(string? Name, string? Url);

public sealed record TargetEntry(string? Kind, string? Target);

public sealed record ApplicationInput(string? Name, string? Description, string? Kind, string? RepoUrl, List<EnvironmentEntry>? Environments,
    List<string>? TechStack, string? VersioningScheme, Guid? TechOwnerUserId, List<TargetEntry>? NonFunctionalTargets);

public sealed record ApplicationSummary(Guid Id, Guid ProjectId, string Name, string? Description, string Kind, IReadOnlyList<string> TechStack,
    string Status, DateTimeOffset CreatedAt);

public sealed record ApplicationView(Guid Id, Guid ProjectId, Guid WorkspaceId, string Name, string? Description, string Kind, string? RepoUrl,
    IReadOnlyList<EnvironmentEntry> Environments, IReadOnlyList<string> TechStack, string VersioningScheme, Guid? TechOwnerUserId,
    string? TechOwnerName, IReadOnlyList<TargetEntry> NonFunctionalTargets, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    bool CanEdit);

public sealed record ComponentInput(string? Name, string? Kind);

public sealed record ComponentView(Guid Id, Guid ApplicationId, string Name, string Kind, string Status, DateTimeOffset CreatedAt);

public sealed record ReleaseInput(string? Name, DateOnly? PlannedDate, string? Status);

public sealed record ReleaseView(Guid Id, Guid ApplicationId, string Name, DateOnly? PlannedDate, string Status, DateTimeOffset CreatedAt);

/// <summary>Maps stored rows to views. The jsonb columns hold the same shapes as the wire.</summary>
internal static class ProjectViews
{
    public static ProjectSummary Summary(ManagedProject p, string? ownerName, int applications) =>
        new(p.Id, p.Name, p.Purpose, p.OwnerUserId, ownerName, p.StartDate, p.EndDate, p.Status, applications, p.CreatedAt, p.SourceConnectionId);

    public static ProjectView View(ManagedProject p, string? ownerName, ProjectAccessView access) =>
        new(p.Id, p.WorkspaceId, p.Name, p.Purpose, p.OwnerUserId, ownerName, p.Sponsor, ProjectJson.Unpack<Stakeholder>(p.Stakeholders),
            p.BudgetAmount, p.BudgetCurrency, p.StartDate, p.EndDate, p.ItemTypes, p.PriorityScale, p.EstimationUnit, p.SprintLengthDays,
            p.DefinitionOfReady, p.DefinitionOfDone, p.Status, p.CreatedAt, p.UpdatedAt, access, p.SourceConnectionId);

    public static ApplicationSummary Summary(ManagedApplication a) =>
        new(a.Id, a.ProjectId, a.Name, a.Description, a.Kind, a.TechStack, a.Status, a.CreatedAt);

    public static ApplicationView View(ManagedApplication a, string? techOwnerName, bool canEdit) =>
        new(a.Id, a.ProjectId, a.WorkspaceId, a.Name, a.Description, a.Kind, a.RepoUrl, ProjectJson.Unpack<EnvironmentEntry>(a.Environments),
            a.TechStack, a.VersioningScheme, a.TechOwnerUserId, techOwnerName, ProjectJson.Unpack<TargetEntry>(a.NonFunctionalTargets), a.Status,
            a.CreatedAt, a.UpdatedAt, canEdit);

    public static ComponentView View(ApplicationComponent c) => new(c.Id, c.ApplicationId, c.Name, c.Kind, c.Status, c.CreatedAt);

    public static ReleaseView View(ApplicationRelease r) => new(r.Id, r.ApplicationId, r.Name, r.PlannedDate, r.Status, r.CreatedAt);
}
