using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Podium.Api.Config;

namespace Podium.Api.Integrations;

public sealed record TokenSet(string Access, string Refresh, int ExpiresInSeconds);

public sealed record AtlassianSite(string Id, string Host, string Name);

/// <summary>
/// Atlassian's OAuth 2.0 (3LO) flow (ADR-0015 §3): the address to send a person to, the code and refresh exchanges, the sites the login can
/// reach and the account behind it. PKCE is sent as well; Atlassian ignores a challenge it does not use. Responses are read for the fields we
/// need only and never logged.
/// </summary>
public sealed partial class JiraOAuth(IHttpClientFactory clients, ApiConfig config, IntegrationSettings settings)
{
    public const string HostSuffix = ".atlassian.net";

    /// <summary>Classic scopes: read and write issues and boards, read people, register webhooks, and a refresh token.</summary>
    public static readonly IReadOnlyList<string> Scopes =
        ["read:jira-work", "write:jira-work", "read:jira-user", "manage:jira-webhook", "offline_access"];

    /// <summary>The Atlassian app in force (environment or Integrations page); callers check <see cref="IntegrationSettings.JiraAvailable"/> first.</summary>
    private OAuthClient App => settings.Jira ?? throw new TrackerException(TrackerException.Codes.Unavailable);

    /// <summary>The callback the Atlassian app registers (under the site's origin).</summary>
    public const string CallbackPath = "/api/integrations/jira/callback";

    public string RedirectUri => config.PublicOrigin + CallbackPath;

    /// <summary>"acme", "acme.atlassian.net" or "https://acme.atlassian.net/jira" to "acme.atlassian.net"; null for anything else.</summary>
    public static string? SiteHost(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = input.Trim().ToLowerInvariant();
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
            text = uri.Host;
        }

        if (text.EndsWith(HostSuffix, StringComparison.Ordinal)) text = text[..^HostSuffix.Length];
        return Label().IsMatch(text) ? text + HostSuffix : null;
    }

    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(48));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public string AuthorizeUrl(string state, string verifier)
    {
        var query = new Dictionary<string, string>
        {
            ["audience"] = "api.atlassian.com",
            ["client_id"] = App.ClientId,
            ["scope"] = string.Join(' ', Scopes),
            ["redirect_uri"] = RedirectUri,
            ["state"] = state,
            ["response_type"] = "code",
            ["prompt"] = "consent",
            ["code_challenge"] = Challenge(verifier),
            ["code_challenge_method"] = "S256",
        };
        return $"{config.AtlassianAuthBase}/authorize?" + string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    public Task<TokenSet> ExchangeAsync(string code, string verifier, CancellationToken ct) => TokenAsync(new Dictionary<string, string>
    {
        ["grant_type"] = "authorization_code",
        ["client_id"] = App.ClientId,
        ["client_secret"] = App.ClientSecret,
        ["code"] = code,
        ["redirect_uri"] = RedirectUri,
        ["code_verifier"] = verifier,
    }, ct);

    public Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken ct) => TokenAsync(new Dictionary<string, string>
    {
        ["grant_type"] = "refresh_token",
        ["client_id"] = App.ClientId,
        ["client_secret"] = App.ClientSecret,
        ["refresh_token"] = refreshToken,
    }, ct);

    public async Task<IReadOnlyList<AtlassianSite>> SitesAsync(string accessToken, CancellationToken ct)
    {
        using var doc = await GetAsync($"{config.AtlassianApiBase}/oauth/token/accessible-resources", accessToken, ct);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        var sites = new List<AtlassianSite>();
        foreach (var s in doc.RootElement.EnumerateArray())
        {
            if (JiraParse.Str(s, "id") is { Length: > 0 } id && JiraParse.Str(s, "url") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps)
            {
                sites.Add(new AtlassianSite(id, uri.Host.ToLowerInvariant(), JiraMapping.Text(JiraParse.Str(s, "name"), 200) ?? uri.Host));
            }
        }

        return sites;
    }

    /// <summary>The Atlassian account the access token belongs to.</summary>
    public async Task<(string AccountId, string DisplayName)> MeAsync(string cloudId, string accessToken, CancellationToken ct)
    {
        using var doc = await GetAsync($"{config.AtlassianApiBase}/ex/jira/{Uri.EscapeDataString(cloudId)}/rest/api/3/myself", accessToken, ct);
        var id = JiraParse.Str(doc.RootElement, "accountId") ?? throw new TrackerException(TrackerException.Codes.Rejected);
        return (id, JiraMapping.Text(JiraParse.Str(doc.RootElement, "displayName"), 200) ?? id);
    }

    private async Task<TokenSet> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{config.AtlassianAuthBase}/oauth/token")
        {
            Content = new StringContent(JsonSerializer.Serialize(form), Encoding.UTF8, "application/json"),
        };
        using var response = await SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // invalid_grant (a used or revoked refresh token, a stale code) is the person's to fix by signing in again.
            throw new TrackerException(response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? TrackerException.Codes.Unauthorized : TrackerException.Codes.Unavailable);
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;
        var access = JiraParse.Str(root, "access_token");
        var refresh = JiraParse.Str(root, "refresh_token");
        var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var n) ? n : 3600;
        if (access is not { Length: > 0 } || refresh is not { Length: > 0 }) throw new TrackerException(TrackerException.Codes.Rejected);
        return new TokenSet(access, refresh, Math.Clamp(seconds, 60, 86400));
    }

    private async Task<JsonDocument> GetAsync(string url, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new TrackerException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? TrackerException.Codes.Unauthorized : TrackerException.Codes.Unavailable);
        }

        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            return await clients.CreateClient(JiraApi.ClientName).SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw new TrackerException(TrackerException.Codes.Unavailable);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TrackerException(TrackerException.Codes.Unavailable);
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex Label();
}
