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
    public const string AgentRoleChanged = "agent.role_changed";
    public const string AgentTagsChanged = "agent.tags_changed";
    public const string AgentNoteChanged = "agent.note_changed";
    public const string AgentServicesChanged = "agent.services_changed";
    public const string AgentInventoryChanged = "agent.inventory_changed";
    public const string AgentAsksChanged = "agent.asks_changed";
    public const string AgentActionRequested = "agent.action_requested";
    public const string AgentClaudeControlChanged = "agent.claude_control_changed";
    public const string AskCreated = "ask.created";
    public const string AskAnswered = "ask.answered";
    public const string AskCancelled = "ask.cancelled";
    public const string RefreshReuse = "agent.refresh_reuse";
    public const string CommandCreated = "session.command_created";
    public const string CommandCancelled = "session.command_cancelled";
    public const string PermissionAnswered = "session.permission_answered";
    public const string RunCreated = "remote.run_created";
    public const string RunApproved = "remote.run_approved";
    public const string RunDenied = "remote.run_denied";
    public const string RunCancelled = "remote.run_cancelled";
    public const string RunFinished = "remote.run_finished";
    public const string GrantRequested = "remote.grant_requested";
    public const string GrantApproved = "remote.grant_approved";
    public const string GrantDenied = "remote.grant_denied";
    public const string GrantRevoked = "remote.grant_revoked";
    public const string JobProposed = "remote.job_proposed";
    public const string JobApproved = "remote.job_approved";
    public const string JobDenied = "remote.job_denied";
    public const string JobRetired = "remote.job_retired";
    public const string ApprovalKeyRegistered = "remote.approval_key_registered";
    public const string ApprovalKeyRetired = "remote.approval_key_retired";
    public const string ScheduleCreated = "remote.schedule_created";
    public const string SchedulePaused = "remote.schedule_paused";
    public const string ScheduleResumed = "remote.schedule_resumed";
    public const string ScheduleEnded = "remote.schedule_ended";
    public const string RemoteSettingsChanged = "remote.settings_changed";
    public const string ProjectCreated = "project.created";
    public const string ProjectUpdated = "project.updated";
    public const string ProjectArchived = "project.archived";
    public const string ProjectRestored = "project.restored";
    public const string ProjectMemberChanged = "project.member_changed";
    public const string ProjectMemberRemoved = "project.member_removed";
    public const string ApplicationCreated = "application.created";
    public const string ApplicationUpdated = "application.updated";
    public const string ApplicationArchived = "application.archived";
    public const string ApplicationRestored = "application.restored";
    public const string ComponentChanged = "application.component_changed";
    public const string ReleaseChanged = "application.release_changed";
    public const string IssueCreated = "issue.created";
    public const string IssueUpdated = "issue.updated";
    public const string IssueArchived = "issue.archived";
    public const string IssueRestored = "issue.restored";
    public const string IssueMoved = "issue.moved";
    public const string IssueLinkChanged = "issue.link_changed";
    public const string IssueTestCaseChanged = "issue.test_case_changed";
    public const string IssueCodeLinkChanged = "issue.code_link_changed";
    public const string IssueRunLinkChanged = "issue.run_link_changed";
    public const string SprintCreated = "sprint.created";
    public const string SprintUpdated = "sprint.updated";
    public const string SprintStarted = "sprint.started";
    public const string SprintClosed = "sprint.closed";
    public const string SprintArchived = "sprint.archived";
    public const string SprintIssuesChanged = "sprint.issues_changed";
    public const string IntegrationConnected = "integration.connected";
    public const string IntegrationAccountLinked = "integration.account_linked";
    public const string IntegrationDisconnected = "integration.disconnected";
    public const string IntegrationChanged = "integration.changed";
    public const string IntegrationImported = "integration.imported";
    public const string IntegrationInvited = "integration.person_invited";
    public const string IntegrationPersonLinked = "integration.person_linked";
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
