using System.Net.Mail;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Security;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>Shared answers and checks. Error codes are i18n keys the web translates (global: no raw strings).</summary>
public static class Http
{
    public static IResult Invalid(string field, string code) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [code] });

    public static IResult NotFound() => Results.NotFound();

    public static bool IsEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254)
        {
            return false;
        }

        try
        {
            return new MailAddress(value).Address == value.Trim();
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool IsName(string? value, int max = 100) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= max;

    /// <summary>Clamps a page size into 1..max; the default is 25.</summary>
    public static int Limit(int? limit, ApiConfig config) => Math.Clamp(limit ?? 25, 1, config.PageSizeMax);

    /// <summary>Starts a web session for the user: a new login_sessions row and the HttpOnly cookie.</summary>
    public static async Task IssueLoginAsync(HttpContext http, MonitorDb db, ApiConfig config, TimeProvider clock, User user)
    {
        ArgumentNullException.ThrowIfNull(http);
        var token = Secrets.NewToken();
        var now = clock.GetUtcNow();
        db.LoginSessions.Add(new LoginSession
        {
            UserId = user.Id,
            TokenHash = Secrets.Hash(token),
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + config.LoginSessionLifetime,
            UserAgent = http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua[..Math.Min(ua.Length, 200)] : null,
        });
        Audit.Add(db, http, clock, AuditActions.SignIn, userId: user.Id);
        await db.SaveChangesAsync(http.RequestAborted);
        http.Response.Cookies.Append(ApiConfig.SessionCookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = config.SecureCookies,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = now + config.LoginSessionLifetime,
        });
    }

    public static string Link(ApiConfig config, string path, string token) =>
        $"{config.PublicOrigin}{path}?token={Uri.EscapeDataString(token)}";
}
