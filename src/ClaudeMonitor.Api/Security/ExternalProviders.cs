using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace ClaudeMonitor.Api.Security;

/// <summary>
/// GitHub and Google sign-in through ASP.NET Core's own OAuth handler (no extra package). Each provider puts the
/// same claims on the short-lived external cookie: subject, e-mail, whether the provider verified it, and a name.
/// A provider without a client id/secret in the environment is simply not registered.
/// </summary>
public static class ExternalProviders
{
    public const string EmailVerifiedClaim = "email_verified";

    public static AuthenticationBuilder AddExternalProviders(this AuthenticationBuilder auth, ApiConfig config)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(config);
        auth.AddCookie(Schemes.External, o =>
        {
            o.Cookie.Name = "cm_external";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = config.SecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        });
        if (config.GitHub is { } gh)
        {
            auth.AddOAuth(Providers.GitHub, o =>
            {
                Common(o, gh, Providers.GitHub);
                o.AuthorizationEndpoint = ProviderEndpoints.GitHubAuthorize;
                o.TokenEndpoint = ProviderEndpoints.GitHubToken;
                o.Scope.Add("read:user");
                o.Scope.Add("user:email");
                o.Events.OnCreatingTicket = GitHubTicket;
            });
        }

        if (config.Google is { } g)
        {
            auth.AddOAuth(Providers.Google, o =>
            {
                Common(o, g, Providers.Google);
                o.AuthorizationEndpoint = ProviderEndpoints.GoogleAuthorize;
                o.TokenEndpoint = ProviderEndpoints.GoogleToken;
                o.Scope.Add("openid");
                o.Scope.Add("email");
                o.Scope.Add("profile");
                o.Events.OnCreatingTicket = GoogleTicket;
            });
        }

        return auth;
    }

    private static void Common(OAuthOptions o, OAuthClient client, string provider)
    {
        o.ClientId = client.ClientId;
        o.ClientSecret = client.ClientSecret;
        o.CallbackPath = ProviderEndpoints.CallbackPrefix + provider;
        o.SignInScheme = Schemes.External;
        o.UsePkce = true;
        o.SaveTokens = false;
    }

    private static async Task GitHubTicket(OAuthCreatingTicketContext ctx)
    {
        using var user = await GetJson(ctx, ProviderEndpoints.GitHubUser);
        using var emails = await GetJson(ctx, ProviderEndpoints.GitHubEmails);
        var primary = emails.RootElement.EnumerateArray()
            .FirstOrDefault(e => e.TryGetProperty("primary", out var p) && p.GetBoolean());
        var root = user.RootElement;
        AddClaims(ctx,
            root.GetProperty("id").GetRawText(),
            primary.ValueKind == JsonValueKind.Object ? primary.GetProperty("email").GetString() : null,
            primary.ValueKind == JsonValueKind.Object && primary.GetProperty("verified").GetBoolean(),
            Str(root, "name") ?? Str(root, "login"));
    }

    private static async Task GoogleTicket(OAuthCreatingTicketContext ctx)
    {
        using var user = await GetJson(ctx, ProviderEndpoints.GoogleUser);
        var root = user.RootElement;
        AddClaims(ctx, root.GetProperty("sub").GetString()!, Str(root, "email"),
            root.TryGetProperty("email_verified", out var v) && v.ValueKind == JsonValueKind.True, Str(root, "name"));
    }

    private static void AddClaims(OAuthCreatingTicketContext ctx, string subject, string? email, bool verified, string? name)
    {
        var id = ctx.Identity!;
        id.AddClaim(new Claim(ClaimTypes.NameIdentifier, subject));
        if (email is not null) id.AddClaim(new Claim(ClaimTypes.Email, email));
        id.AddClaim(new Claim(EmailVerifiedClaim, verified ? "true" : "false"));
        if (name is not null) id.AddClaim(new Claim(ClaimTypes.Name, name));
    }

    private static async Task<JsonDocument> GetJson(OAuthCreatingTicketContext ctx, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);
        request.Headers.UserAgent.ParseAdd("claude-monitor");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await ctx.Backchannel.SendAsync(request, ctx.HttpContext.RequestAborted);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ctx.HttpContext.RequestAborted));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
