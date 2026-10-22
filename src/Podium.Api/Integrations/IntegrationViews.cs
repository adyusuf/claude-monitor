using Podium.Api.Data;

namespace Podium.Api.Integrations;

/// <summary>What the web gets about a connection. No token, no secret, no tool text beyond a display name.</summary>
public sealed record ConnectionView(
    Guid Id,
    string Provider,
    string SiteHost,
    string Status,
    bool AutoInvite,
    string ConnectorName,
    bool ConnectedByMe,
    bool MyAccountLinked,
    string? MyAccountStatus,
    bool WebhookActive,
    DateTimeOffset? WebhookExpiresAt,
    DateTimeOffset? LastPassAt,
    string? LastError,
    int ProjectCount);

public sealed record IntegrationsView(bool Available, IReadOnlyList<string> TrackerProviders, IReadOnlyList<ConnectionView> Connections);

public sealed record ConnectRequest(string? Subdomain, string? Code);

public sealed record ConnectResponse(string AuthorizeUrl);

public sealed record ConnectionSettings(bool AutoInvite);

public static class IntegrationViews
{
    public static ConnectionView Of(IntegrationConnection c, string connectorName, Guid me, IntegrationAccount? mine, int projects) => new(
        c.Id,
        c.Provider,
        c.SiteHost,
        c.Status,
        c.AutoInvite,
        connectorName,
        c.ConnectorUserId == me,
        mine is not null,
        mine?.Status,
        c.WebhookIds is not null && c.WebhookExpiresAt is { } e && e > DateTimeOffset.UnixEpoch,
        c.WebhookExpiresAt,
        c.LastPassAt,
        c.LastError,
        projects);
}
