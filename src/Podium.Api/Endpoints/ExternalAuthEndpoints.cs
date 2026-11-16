using System.Security.Claims;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Projects;
using Podium.Api.Security;
using Podium.Api.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>
/// Sign-in and linking with GitHub or Google (ADR-0002). A provider sign-in NEVER silently joins an existing
/// account: if its verified e-mail belongs to a user who has not linked this provider, that user is sent to sign in
/// the usual way and link it from settings. Every answer here is a redirect to a page of the web app.
/// </summary>
public static class ExternalAuthEndpoints
{
    public const string ModeSignIn = "signin";
    public const string ModeLink = "link";

    /// <summary>The settings page's error after a link whose sign-in was not fresh (F-181).</summary>
    public const string LinkReauthRequired = "link_reauth_required";

    /// <summary>409 title: unlinking would leave the account no way to sign in (no password and no other provider).</summary>
    public const string LastSignInMethod = "last_sign_in_method";

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/auth/external/{provider}", Start).RequireRateLimiting(AuthEndpoints.RateLimitPolicy).AllowAnonymous();
        api.MapGet("/auth/external/{provider}/done", Done).AllowAnonymous();
        api.MapDelete("/me/providers/{provider}", Unlink).RequireAuthorization(Schemes.Session).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
    }

    /// <summary>
    /// Removes a linked provider from the signed-in account (F-181). Needs a fresh sign-in, like linking; refused when it is the last way in
    /// (no password and no other provider). Judged under a lock on the user's row, so two unlinks at once cannot each leave the other as the
    /// way that stays.
    /// </summary>
    private static async Task<IResult> Unlink(string provider, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock)
    {
        if (!Providers.All.Contains(provider)) return Http.NotFound();
        var userId = http.User.UserId();
        var ct = http.RequestAborted;
        if (!await PrivacyEndpoints.RecentSignInAsync(http, db, config, clock))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: Remote.RemoteChecks.ReauthRequired);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM users WHERE id = {userId} FOR UPDATE", ct);
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var providers = await db.UserLogins.Where(l => l.UserId == userId).Select(l => l.Provider).ToListAsync(ct);
        if (!providers.Contains(provider)) return Http.NotFound();
        if (user.PasswordHash is null && providers.All(p => p == provider))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: LastSignInMethod);
        }

        await db.UserLogins.Where(l => l.UserId == userId && l.Provider == provider).ExecuteDeleteAsync(ct);
        Audit.Add(db, http, clock, AuditActions.ProviderUnlinked, userId: userId, detail: new { provider });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static IResult Start(string provider, string? mode, string? next, IntegrationSettings settings)
    {
        if (!Providers.All.Contains(provider) || settings.Provider(provider) is null)
        {
            return Http.NotFound();
        }

        var safeMode = mode == ModeLink ? ModeLink : ModeSignIn;
        // The page a sign-in started for (an invitation link) rides in the protected state; linking always returns to settings.
        var target = safeMode == ModeSignIn ? SafeNext(next) : "/";
        return Results.Challenge(new AuthenticationProperties
        {
            RedirectUri = $"/api/auth/external/{provider}/done?mode={safeMode}{NextQuery(target)}",
        }, [provider]);
    }

    private static async Task<IResult> Done(string provider, string? mode, string? next, HttpContext http, PodiumDb db, ApiConfig config,
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
        // The name the provider gives passes the rule a typed one does; one that fails it (blank, too long, a control character) is replaced by the address.
        var suppliedName = external.Principal.FindFirstValue(ClaimTypes.Name);
        var name = Http.IsName(suppliedName) ? suppliedName! : email ?? provider;
        var existing = await db.UserLogins.FirstOrDefaultAsync(
            l => l.Provider == provider && l.ProviderSubject == subject, http.RequestAborted);
        var now = clock.GetUtcNow();
        var target = SafeNext(next); // checked again: this address can be called without the state

        if (mode == ModeLink)
        {
            var session = await http.AuthenticateAsync(Schemes.Session);
            if (!session.Succeeded) return Back(config, "/login?error=sign_in_first");
            var userId = session.Principal!.UserId();
            // A session cookie alone links nothing (F-181): a stolen one would attach its own way in. The session is this request's user from
            // here, so the fresh sign-in check reads it.
            http.User = session.Principal;
            if (!await PrivacyEndpoints.RecentSignInAsync(http, db, config, clock)) return Back(config, "/settings?error=" + LinkReauthRequired);
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
                // In a cookie only the second step receives, not in the address (history, logs, Referer).
                http.Response.Cookies.Append(ApiConfig.MfaCookie, token, MfaEndpoints.PendingCookie(config, now + config.MfaPendingLifetime));
                return Back(config, target == "/" ? "/mfa" : "/mfa?next=" + Uri.EscapeDataString(target));
            }

            await Http.IssueLoginAsync(http, db, config, clock, user);
            return Back(config, target);
        }

        if (email is null || !verified) return Back(config, "/login?error=provider_email_unverified");
        var key = EmailIdentity.Key(email);
        if (await db.Users.AnyAsync(u => u.EmailKey == key, http.RequestAborted))
        {
            return Back(config, "/login?error=link_required&provider=" + provider);
        }

        var created = Accounts.NewUser(db, now, email, name, verified: true);
        AddLogin(db, created.Id, provider, subject, email, now);
        Audit.Add(db, http, clock, AuditActions.Register, userId: created.Id, detail: new { provider });
        try
        {
            await db.SaveChangesAsync(http.RequestAborted);
        }
        catch (DbUpdateException e) when (UniqueViolation.Is(e))
        {
            // Another account holds a look-alike of the address while email_normalized is still unique (F-180), or the same sign-up won a race.
            return Back(config, "/login?error=link_required&provider=" + provider);
        }

        await Http.IssueLoginAsync(http, db, config, clock, created);
        return Back(config, target);
    }

    /// <summary>
    /// A same-site path to return to, else the start page: it must start with one slash, and hold no second slash or
    /// backslash right after it, no backslash and no control character (browsers read those as another host). The same
    /// rule as the web's safeNext.
    /// </summary>
    internal static string SafeNext(string? next) =>
        next is { Length: > 0 } && next[0] == '/' && (next.Length == 1 || next[1] != '/')
            && !next.Any(c => c == '\\' || char.IsControl(c))
            ? next
            : "/";

    private static string NextQuery(string target) => target == "/" ? "" : "&next=" + Uri.EscapeDataString(target);

    private static void AddLogin(PodiumDb db, Guid userId, string provider, string subject, string? email, DateTimeOffset now) =>
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
