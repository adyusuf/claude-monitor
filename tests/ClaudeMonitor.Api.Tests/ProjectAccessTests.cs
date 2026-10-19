using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Projects;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The whole table of who may do what with a project (ADR-0011 §2), without a database.</summary>
public sealed class ProjectAccessTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Someone = Guid.NewGuid();

    private static ProjectGrant Decide(string workspaceRole, Guid user, string? projectRole) =>
        ProjectAccess.Decide(new ManagedProject { OwnerUserId = Owner }, new WorkspaceMember { UserId = user, Role = workspaceRole }, projectRole);

    // workspace role, is the project's owner, project role -> edit, archive, edit applications
    [Theory]
    [InlineData(Roles.Owner, false, null, true, true, true)]
    [InlineData(Roles.Admin, false, null, true, true, true)]
    [InlineData(Roles.Member, true, null, true, true, true)]
    [InlineData(Roles.Member, false, ProjectRoles.Manager, true, false, true)]
    [InlineData(Roles.Member, false, ProjectRoles.Contributor, false, false, true)]
    [InlineData(Roles.Member, false, ProjectRoles.Reviewer, false, false, false)]
    [InlineData(Roles.Member, false, ProjectRoles.Viewer, false, false, false)]
    [InlineData(Roles.Member, false, null, false, false, false)]
    [InlineData(Roles.Viewer, true, ProjectRoles.Manager, false, false, false)] // a workspace viewer never changes anything
    [InlineData(Roles.Viewer, false, ProjectRoles.Contributor, false, false, false)]
    [InlineData("unknown-role", true, ProjectRoles.Manager, false, false, false)] // fail-closed
    [InlineData(Roles.Member, false, "unknown-project-role", false, false, false)]
    public void Each_combination_of_roles_gets_exactly_its_rights(string workspaceRole, bool isOwner, string? projectRole, bool edit, bool archive, bool applications)
    {
        var grant = Decide(workspaceRole, isOwner ? Owner : Someone, projectRole);

        Assert.Equal((edit, archive, applications), (grant.CanEdit, grant.CanArchive, grant.CanEditApplications));
        Assert.Equal(new ProjectAccessView(edit, archive, applications), grant.View);
    }
}
