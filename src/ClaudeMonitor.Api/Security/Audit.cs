using System.Text.Json;
using ClaudeMonitor.Api.Data;

namespace ClaudeMonitor.Api.Security;

/// <summary>Audit actions (docs/data-model.md §6). The detail never carries captured content, passwords or tokens.</summary>
public static class AuditActions
{
    public const string Register = "user.register";
    public const string EmailVerified = "user.email_verified";
    public const string SignIn = "user.sign_in";
    public const string SignInFailed = "user.sign_in_failed";
    public const string AccountLocked = "user.account_locked";
    public const string MfaEnabled = "user.mfa_enabled";
    public const string MfaDisabled = "user.mfa_disabled";
    public const string SignOut = "user.sign_out";
    public const string PasswordReset = "user.password_reset";
    public const string ProviderLinked = "user.provider_linked";
    public const string AccountDeleted = "user.account_deleted";
    public const string WorkspaceCreated = "workspace.created";
    public const string InvitationCreated = "workspace.invitation_created";
    public const string InvitationAccepted = "workspace.invitation_accepted";
    public const string InvitationRevoked = "workspace.invitation_revoked";
    public const string RoleChanged = "workspace.role_changed";
    public const string MemberRemoved = "workspace.member_removed";
    public const string SettingsChanged = "workspace.settings_changed";
    public const string DeviceApproved = "agent.device_approved";
    public const string DeviceDenied = "agent.device_denied";
    public const string AgentRevoked = "agent.revoked";
    public const string AgentMoved = "agent.moved";
    public const string RefreshReuse = "agent.refresh_reuse";
    public const string CommandCreated = "session.command_created";
    public const string CommandCancelled = "session.command_cancelled";
    public const string PermissionAnswered = "session.permission_answered";
}

public static class Audit
{
    /// <summary>Adds an audit row to the unit of work; it is saved with the change it records.</summary>
    public static void Add(MonitorDb db, HttpContext? http, TimeProvider clock, string action,
        Guid? workspaceId = null, Guid? userId = null, Guid? agentId = null,
        string? targetType = null, object? targetId = null, object? detail = null)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            WorkspaceId = workspaceId,
            ActorUserId = userId,
            ActorAgentId = agentId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId?.ToString(),
            At = clock.GetUtcNow(),
            IpHash = Secrets.IpTag(http?.Connection.RemoteIpAddress),
            Detail = detail is null ? null : JsonSerializer.SerializeToDocument(detail),
        });
    }
}
