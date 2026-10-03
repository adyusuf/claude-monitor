using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Text;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>Creating users and workspaces: both sign-up paths (password, provider) go through here.</summary>
public static class Accounts
{
    /// <summary>A new user, with a first workspace they own. Not saved: the caller saves it with the rest.</summary>
    public static User NewUser(MonitorDb db, DateTimeOffset now, string email, string displayName, bool verified)
    {
        ArgumentNullException.ThrowIfNull(db);
        var name = displayName.Trim();
        var user = new User
        {
            Email = email.Trim(),
            EmailNormalized = SearchText.Email(email),
            DisplayName = name,
            DisplayNameSearch = SearchText.Normalize(name),
            EmailVerifiedAt = verified ? now : null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Users.Add(user);
        NewWorkspace(db, now, user.Id, name);
        return user;
    }

    public static Workspace NewWorkspace(MonitorDb db, DateTimeOffset now, Guid ownerId, string name)
    {
        ArgumentNullException.ThrowIfNull(db);
        var workspace = new Workspace
        {
            Name = name.Trim(),
            NameSearch = SearchText.Normalize(name),
            CreatedBy = ownerId,
            CreatedAt = now,
        };
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            WorkspaceId = workspace.Id,
            UserId = ownerId,
            Role = Roles.Owner,
            JoinedAt = now,
        });
        db.WorkspaceSettings.Add(new WorkspaceSettings { WorkspaceId = workspace.Id, UpdatedAt = now, UpdatedBy = ownerId });
        return workspace;
    }
}
