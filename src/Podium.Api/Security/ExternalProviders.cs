using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Podium.Api.Config;
using Podium.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;

namespace Podium.Api.Security;

/// <summary>
/// GitHub and Google sign-in through ASP.NET Core's own OAuth handler (no extra package). Each provider puts the
/// same claims on the short-lived external cookie: subject, e-mail, whether the provider verified it, and a name.
/// Both are always registered; whether one is offered follows the running settings (ADR-0017).
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
            o.Cookie.Name = "ap_external";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = config.SecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        });
        auth.AddOAuth(Providers.GitHub, o =>
        {
            Common(o, Providers.GitHub);
            o.AuthorizationEndpoint = ProviderEndpoints.GitHubAuthorize;
            o.TokenEndpoint = ProviderEndpoints.GitHubToken;
            o.Scope.Add("read:user");
            o.Scope.Add("user:email");
            o.Events.OnCreatingTicket = GitHubTicket;
        });
        auth.AddOAuth(Providers.Google, o =>
        {
            Common(o, Providers.Google);
            o.AuthorizationEndpoint = ProviderEndpoints.GoogleAuthorize;
            o.TokenEndpoint = ProviderEndpoints.GoogleToken;
            o.Scope.Add("openid");
            o.Scope.Add("email");
            o.Scope.Add("profile");
            o.Events.OnCreatingTicket = GoogleTicket;
        });
        foreach (var provider in Providers.All)
        {
            auth.Services.AddOptions<OAuthOptions>(provider).Configure<IntegrationSettings>((o, settings) => App(o, settings.Provider(provider)));
        }

        return auth;
    }

    /// <summary>
    /// The app values, read again whenever <see cref="IntegrationSettings"/> drops the cached options. A provider that is not set still gets
    /// placeholders, because the handler validates its options on every request it looks at; it is not offered (/auth/providers) and its
    /// sign-in start answers 404 (ADR-0017).
    /// </summary>
    internal static void App(OAuthOptions o, OAuthClient? client)
    {
        o.ClientId = client?.ClientId ?? NotConfigured;
        o.ClientSecret = client?.ClientSecret ?? NotConfigured;
    }

    internal const string NotConfigured = "not-configured";

    /// <summary>
    /// A callback that fails (no or wrong state, the person said no at the provider, the code was refused). Without this the handler throws, which
    /// is a 500 and an error in the log for every stray request. A provider that is not set answers 404 like any unknown route; otherwise the
    /// person goes back to the sign-in page (ADR-0017).
    /// </summary>
    internal static Task RemoteFailure(RemoteFailureContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var services = ctx.HttpContext.RequestServices;
        if (services.GetRequiredService<IntegrationSettings>().Provider(ctx.Scheme.Name) is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        }
        else
        {
            // The framework logs a handled failure at Information only, which production drops: a mistyped client secret would fail every
            // sign-in without a trace. The type of the failure, never its message or the provider's answer (they can carry the code or token).
            var log = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ExternalProviders).FullName!);
            if (ctx.Request.Query["error"] == "access_denied") log.LogInformation("Sign-in with {Provider} was cancelled by the person", ctx.Scheme.Name);
            else log.LogWarning("Sign-in with {Provider} failed: {Type}", ctx.Scheme.Name, ctx.Failure?.GetType().Name ?? "unknown");

            // Linking from Settings goes back to Settings: the person is signed in already, the sign-in form would be a surprise.
            var back = ctx.Properties?.RedirectUri?.Contains("mode=link", StringComparison.Ordinal) == true ? "/settings" : "/login";
            ctx.Response.Redirect(services.GetRequiredService<ApiConfig>().PublicOrigin + back + "?error=provider_failed");
        }

        ctx.HandleResponse();
        return Task.CompletedTask;
    }

    private static void Common(OAuthOptions o, string provider)
    {
        o.Events.OnRemoteFailure = RemoteFailure;
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
        request.Headers.UserAgent.ParseAdd("aipod");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await ctx.Backchannel.SendAsync(request, ctx.HttpContext.RequestAborted);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ctx.HttpContext.RequestAborted));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
