using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Projects;

/// <summary>What the caller may do with one project (ADR-0011 §2). Fail-closed: no workspace membership means no project.</summary>
public sealed record ProjectGrant(ManagedProject Project, WorkspaceMember Member, bool CanEdit, bool CanArchive, bool CanEditApplications)
{
    public ProjectAccessView View => new(CanEdit, CanArchive, CanEditApplications);
}

public static class ProjectAccess
{
    /// <summary>The project and the caller's rights, or null when the caller cannot see it (answer 404).</summary>
    public static async Task<ProjectGrant?> OfAsync(MonitorDb db, Guid userId, Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null)
        {
            return null;
        }

        var member = await Security.Access.MemberAsync(db, userId, project.WorkspaceId, Roles.Viewer, ct);
        if (member is null)
        {
            return null;
        }

        var projectRole = await db.ProjectMembers.AsNoTracking().Where(m => m.ProjectId == projectId && m.UserId == userId)
            .Select(m => m.Role).FirstOrDefaultAsync(ct);
        return Decide(project, member, projectRole);
    }

    /// <summary>An application and the caller's rights on its project, or null when the caller cannot see it (answer 404).</summary>
    public static async Task<(ManagedApplication App, ProjectGrant Grant)?> ApplicationAsync(MonitorDb db, Guid userId, Guid applicationId,
        CancellationToken ct)
    {
        var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == applicationId, ct);
        if (app is null)
        {
            return null;
        }

        var grant = await OfAsync(db, userId, app.ProjectId, ct);
        return grant is null ? null : (app, grant);
    }

    /// <summary>An issue and the caller's rights on its project, or null when the caller cannot see it (answer 404).</summary>
    public static async Task<(Issue Issue, ProjectGrant Grant)?> IssueAsync(MonitorDb db, Guid userId, Guid issueId, CancellationToken ct)
    {
        var issue = await db.Issues.FirstOrDefaultAsync(i => i.Id == issueId, ct);
        if (issue is null)
        {
            return null;
        }

        var grant = await OfAsync(db, userId, issue.ProjectId, ct);
        return grant is null ? null : (issue, grant);
    }

    /// <summary>A sprint (not an archived one) and the caller's rights on its project, or null when the caller cannot see it (answer 404).</summary>
    public static async Task<(Sprint Sprint, ProjectGrant Grant)?> SprintAsync(MonitorDb db, Guid userId, Guid sprintId, CancellationToken ct)
    {
        var sprint = await db.Sprints.FirstOrDefaultAsync(s => s.Id == sprintId && s.ArchivedAt == null, ct);
        if (sprint is null)
        {
            return null;
        }

        var grant = await OfAsync(db, userId, sprint.ProjectId, ct);
        return grant is null ? null : (sprint, grant);
    }

    /// <summary>The rules alone, so a test can walk the whole table of roles.</summary>
    public static ProjectGrant Decide(ManagedProject project, WorkspaceMember member, string? projectRole)
    {
        // A workspace viewer reads everywhere and changes nothing, whatever the project says.
        var writes = Roles.Rank(member.Role) >= Roles.Rank(Roles.Member);
        var admin = Roles.Rank(member.Role) >= Roles.Rank(Roles.Admin);
        var owner = project.OwnerUserId == member.UserId;
        var manager = owner || admin || projectRole == ProjectRoles.Manager;
        return new ProjectGrant(project, member,
            CanEdit: writes && manager,
            CanArchive: writes && (owner || admin),
            CanEditApplications: writes && (manager || projectRole == ProjectRoles.Contributor));
    }
}
