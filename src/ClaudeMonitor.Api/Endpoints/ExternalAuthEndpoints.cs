using System.Security.Claims;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>
/// Sign-in and linking with GitHub or Google (ADR-0002). A provider sign-in NEVER silently joins an existing
/// account: if its verified e-mail belongs to a user who has not linked this provider, that user is sent to sign in
/// the usual way and link it from settings. Every answer here is a redirect to a page of the web app.
/// </summary>
public static class ExternalAuthEndpoints
{
    public const string ModeSignIn = "signin";
    public const string ModeLink = "link";

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/auth/external/{provider}", Start).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        api.MapGet("/auth/external/{provider}/done", Done);
    }

    private static async Task<IResult> Start(string provider, string? mode, HttpContext http)
    {
        var schemes = http.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>();
        if (!Providers.All.Contains(provider) || await schemes.GetSchemeAsync(provider) is null)
        {
            return Http.NotFound();
        }

        var safeMode = mode == ModeLink ? ModeLink : ModeSignIn;
        return Results.Challenge(new AuthenticationProperties
        {
            RedirectUri = $"/api/auth/external/{provider}/done?mode={safeMode}",
        }, [provider]);
    }

    private static async Task<IResult> Done(string provider, string? mode, HttpContext http, MonitorDb db, ApiConfig config,
        TimeProvider clock)
    {
        var external = await http.AuthenticateAsync(Schemes.External);
        await http.SignOutAsync(Schemes.External);
        var subject = external.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!external.Succeeded || subject is null || !Providers.All.Contains(provider))
        {
            return Back(config, "/login?error=provider_failed");
        }

        var email = external.Principal!.FindFirstValue(ClaimTypes.Email);
        var verified = external.Principal.FindFirstValue(ExternalProviders.EmailVerifiedClaim) == "true";
        var name = external.Principal.FindFirstValue(ClaimTypes.Name) ?? email ?? provider;
        var existing = await db.UserLogins.FirstOrDefaultAsync(
            l => l.Provider == provider && l.ProviderSubject == subject, http.RequestAborted);
        var now = clock.GetUtcNow();

        if (mode == ModeLink)
        {
            var session = await http.AuthenticateAsync(Schemes.Session);
            if (!session.Succeeded) return Back(config, "/login?error=sign_in_first");
            var userId = session.Principal!.UserId();
            if (existing is not null)
            {
                return Back(config, existing.UserId == userId ? "/settings?linked=" + provider : "/settings?error=provider_in_use");
            }

            AddLogin(db, userId, provider, subject, email, now);
            Audit.Add(db, http, clock, AuditActions.ProviderLinked, userId: userId, detail: new { provider });
            await db.SaveChangesAsync(http.RequestAborted);
            return Back(config, "/settings?linked=" + provider);
        }

        if (existing is not null)
        {
            var user = await db.Users.FirstAsync(u => u.Id == existing.UserId, http.RequestAborted);
            if (user.Status != UserStatuses.Active) return Back(config, "/login?error=account_disabled");
            if (user.TotpEnabledAt is not null)
            {
                var token = MfaEndpoints.Pending(db, config, user, now);
                await db.SaveChangesAsync(http.RequestAborted);
                return Back(config, "/mfa?token=" + Uri.EscapeDataString(token));
            }

            await Http.IssueLoginAsync(http, db, config, clock, user);
            return Back(config, "/");
        }

        if (email is null || !verified) return Back(config, "/login?error=provider_email_unverified");
        var normalized = SearchText.Email(email);
        if (await db.Users.AnyAsync(u => u.EmailNormalized == normalized, http.RequestAborted))
        {
            return Back(config, "/login?error=link_required&provider=" + provider);
        }

        var created = Accounts.NewUser(db, now, email, name, verified: true);
        AddLogin(db, created.Id, provider, subject, email, now);
        Audit.Add(db, http, clock, AuditActions.Register, userId: created.Id, detail: new { provider });
        await db.SaveChangesAsync(http.RequestAborted);
        await Http.IssueLoginAsync(http, db, config, clock, created);
        return Back(config, "/");
    }

    private static void AddLogin(MonitorDb db, Guid userId, string provider, string subject, string? email, DateTimeOffset now) =>
        db.UserLogins.Add(new UserLogin
        {
            UserId = userId,
            Provider = provider,
            ProviderSubject = subject,
            ProviderEmail = email,
            LinkedAt = now,
        });

    private static IResult Back(ApiConfig config, string path) => Results.Redirect(config.PublicOrigin + path);
}
