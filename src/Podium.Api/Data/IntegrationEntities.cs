namespace Podium.Api.Data;

// Work tracker connections and what they mirror (ADR-0015, docs/data-model.md §8).

public static class ConnectionStatuses
{
    public const string Active = "active";
    public const string Reauthorize = "reauthorize";
    public const string Disconnected = "disconnected";
    public static readonly IReadOnlyList<string> All = [Active, Reauthorize, Disconnected];
}

public static class AccountStatuses
{
    public const string Active = "active";
    public const string Reauthorize = "reauthorize";
    public static readonly IReadOnlyList<string> All = [Active, Reauthorize];
}

public static class OAuthPurposes
{
    public const string Connect = "connect";
    public const string Account = "account";
    public static readonly IReadOnlyList<string> All = [Connect, Account];
}

public static class LinkKinds
{
    public const string Project = "project";
    public const string Issue = "issue";
    public const string Sprint = "sprint";
    public static readonly IReadOnlyList<string> All = [Project, Issue, Sprint];
}

public static class SyncKinds
{
    public const string Import = "import";
    public const string Webhook = "webhook";
    public const string Pass = "pass";
    public static readonly IReadOnlyList<string> All = [Import, Webhook, Pass];
}

public static class SyncStatuses
{
    public const string Running = "running";
    public const string Ok = "ok";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public static readonly IReadOnlyList<string> All = [Running, Ok, Partial, Failed];
}

public sealed class IntegrationConnection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public string Provider { get; set; } = "";
    public string SiteHost { get; set; } = "";
    public string CloudId { get; set; } = "";
    /// <summary>The user whose Atlassian account reads and syncs for everybody.</summary>
    public Guid ConnectorUserId { get; set; }
    public bool AutoInvite { get; set; }
    public string Status { get; set; } = ConnectionStatuses.Active;
    /// <summary>The ids the tool returned for the webhooks Podium registered (jsonb array of numbers).</summary>
    public string? WebhookIds { get; set; }
    public DateTimeOffset? WebhookExpiresAt { get; set; }
    public DateTimeOffset? LastPassAt { get; set; }
    public string? LastError { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class IntegrationAccount
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ConnectionId { get; set; }
    public Guid UserId { get; set; }
    public string ExternalAccountId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Sealed with the integration key; never read back for display.</summary>
    public string RefreshToken { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public DateTimeOffset AccessExpiresAt { get; set; }
    public string Status { get; set; } = AccountStatuses.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class IntegrationOAuthState
{
    public string StateHash { get; set; } = "";
    public Guid UserId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ConnectionId { get; set; }
    public string Purpose { get; set; } = OAuthPurposes.Connect;
    public string SiteHost { get; set; } = "";
    public string Verifier { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class IntegrationLink
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ConnectionId { get; set; }
    public string Kind { get; set; } = LinkKinds.Issue;
    public string ExternalId { get; set; } = "";
    public string? ExternalKey { get; set; }
    public Guid LocalId { get; set; }
    /// <summary>The tool's own change value (Jira's <c>updated</c>), compared to skip what has not changed.</summary>
    public string? ExternalUpdated { get; set; }
    public DateTimeOffset SyncedAt { get; set; }
}

public sealed class IntegrationPerson
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ConnectionId { get; set; }
    public string ExternalAccountId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public string? EmailNormalized { get; set; }
    public Guid? UserId { get; set; }
    public DateTimeOffset? InvitedAt { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class IntegrationSyncRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ConnectionId { get; set; }
    public string Kind { get; set; } = SyncKinds.Pass;
    public string Status { get; set; } = SyncStatuses.Running;
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Archived { get; set; }
    public int People { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

public sealed class IntegrationWebhookReceipt
{
    public Guid ConnectionId { get; set; }
    public string DeliveryId { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
}
