using System.Text.Json;

namespace ClaudeMonitor.Contracts;

// The protocol between an agent and the API (ADR-0002). Agents in the field are old clients, so every type
// here only GROWS: a field is never removed, renamed or retyped (global #4). New fields are optional.

/// <summary>Request headers every agent call carries.</summary>
public static class AgentHeaders
{
    public const string Version = "X-Agent-Version";
}

/// <summary>One numbered batch of captured events. The API stores a batch number once per agent.</summary>
public sealed record EventBatch(long BatchSeq, IReadOnlyList<CapturedEvent> Events);

/// <summary>One thing a harness reported, with the session context the agent knows at that moment.</summary>
public sealed record CapturedEvent(
    string HarnessKind,
    string SessionExternalId,
    string Kind,
    DateTimeOffset OccurredAt,
    JsonElement Payload,
    bool Truncated = false,
    string? ProjectKey = null,
    string? ProjectName = null,
    string? GitBranch = null);

public sealed record BatchAck(long BatchSeq, bool Duplicate, int Stored);

public sealed record DeviceCodeRequest(string MachineKey, string Hostname, string Os, string Arch, string AgentVersion);

public sealed record DeviceCodeResponse(
    string DeviceCode, string UserCode, string VerificationUri, int IntervalSeconds, int ExpiresInSeconds);

public sealed record DeviceTokenRequest(string DeviceCode);

public sealed record RefreshRequest(string RefreshToken);

public sealed record TokenResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessExpiresAt,
    DateTimeOffset RefreshExpiresAt,
    Guid AgentId,
    Guid WorkspaceId);

/// <summary>The RFC 8628 error body of the device token endpoint.</summary>
public sealed record DeviceTokenError(string Error);

public static class DeviceTokenErrors
{
    public const string Pending = "authorization_pending";
    public const string SlowDown = "slow_down";
    public const string Denied = "access_denied";
    public const string Expired = "expired_token";
}

/// <summary>A tool call waiting for a person's answer, sent the moment the harness asks.</summary>
public sealed record PermissionRequestCreate(
    string HarnessKind, string SessionExternalId, string ToolName, JsonElement ToolInput, int WaitSeconds);

public sealed record PermissionRequestCreated(Guid Id, DateTimeOffset ExpiresAt);

/// <summary>A message on the agent's own event stream (server-sent events, type in the event name).</summary>
public sealed record AgentCommandMessage(Guid Id, Guid SessionId, string SessionExternalId, string Kind, string? Body,
    DateTimeOffset ExpiresAt);

public sealed record PermissionAnswerMessage(Guid Id, string SessionExternalId, string Decision, string? Reason);

public sealed record CommandStatusUpdate(string Status, string? Result);

/// <summary>What the agent must know of its workspace's settings before it captures anything.</summary>
public sealed record AgentSettings(bool MaskSecrets, int EventMaxBytes, Guid WorkspaceId);

public static class AgentStreamEvents
{
    public const string Command = "command";
    public const string PermissionAnswer = "permission_answer";
    public const string Revoked = "revoked";
    public const string Ping = "ping";

    /// <summary>The first message of every connection: it makes the server send its headers at once, so the agent knows it is connected.</summary>
    public const string Ready = "ready";
}
