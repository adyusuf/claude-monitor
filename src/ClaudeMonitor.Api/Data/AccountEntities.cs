namespace ClaudeMonitor.Api.Data;

// Accounts and workspaces (docs/data-model.md §1-2). Status and role strings are the closed sets below;
// the database holds the same values behind CHECK constraints.

public static class UserStatuses
{
    public const string Active = "active";
    public const string Disabled = "disabled";
    public const string Deleted = "deleted";
}

public static class Providers
{
    public const string GitHub = "github";
    public const string Google = "google";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { GitHub, Google };
}

public static class TokenPurposes
{
    public const string VerifyEmail = "verify_email";
    public const string ResetPassword = "reset_password";
    public const string MfaPending = "mfa_pending";
}

public static class WorkspaceStatuses
{
    public const string Active = "active";
}

public static class Roles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";
    public const string Viewer = "viewer";
    public static readonly IReadOnlyList<string> All = [Owner, Admin, Member, Viewer];

    /// <summary>Higher is stronger. An unknown role ranks below every known one (fail-closed).</summary>
    public static int Rank(string role) => role switch
    {
        Owner => 4,
        Admin => 3,
        Member => 2,
        Viewer => 1,
        _ => 0,
    };
}

public sealed class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string? Email { get; set; }
    public string? EmailNormalized { get; set; }
    public string DisplayName { get; set; } = "";
    public string DisplayNameSearch { get; set; } = "";
    public string? PasswordHash { get; set; }
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    public string Status { get; set; } = UserStatuses.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int FailedSignIns { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>The TOTP secret, sealed (SecretBox); set while being set up, kept once enabled.</summary>
    public string? TotpSecret { get; set; }
    public DateTimeOffset? TotpEnabledAt { get; set; }
    public long? TotpLastStep { get; set; }
}

public sealed class UserRecoveryCode
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string CodeHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class UserLogin
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string Provider { get; set; } = "";
    public string ProviderSubject { get; set; } = "";
    public string? ProviderEmail { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
}

public sealed class UserToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string Purpose { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class LoginSession
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? UserAgent { get; set; }
}

public sealed class Workspace
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";
    public string NameSearch { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = WorkspaceStatuses.Active;
}

public sealed class WorkspaceMember
{
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = Roles.Member;
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
}

public sealed class WorkspaceInvitation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; set; }
    public string EmailNormalized { get; set; } = "";
    public string Role { get; set; } = Roles.Member;
    public string TokenHash { get; set; } = "";
    public Guid InvitedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class WorkspaceSettings
{
    public Guid WorkspaceId { get; set; }
    public bool MaskSecrets { get; set; } = true;
    public int RetentionDays { get; set; } = 90;
    public int EventMaxBytes { get; set; } = 262_144;
    public int AlertCpuPct { get; set; } = 90;
    public int AlertMemoryPct { get; set; } = 90;
    public int AlertDiskPct { get; set; } = 90;
    public int AlertSustainSeconds { get; set; } = 300;
    public bool RemoteRunsEnabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}
