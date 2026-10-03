using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Mail;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Text;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

public sealed record RegisterRequest(string? Email, string? Password, string? DisplayName);
public sealed record LoginRequest(string? Email, string? Password);
public sealed record TokenRequest(string? Token);
public sealed record ForgotRequest(string? Email);
public sealed record ResetRequest(string? Token, string? Password);

/// <summary>E-mail and password accounts. Answers never reveal whether an address is registered.</summary>
public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    public static void Map(RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").RequireRateLimiting(RateLimitPolicy);
        auth.MapPost("/register", Register);
        auth.MapPost("/verify-email", VerifyEmail);
        auth.MapPost("/login", Login);
        auth.MapPost("/password/forgot", Forgot);
        auth.MapPost("/password/reset", Reset);
        api.MapPost("/auth/logout", Logout).RequireAuthorization(Schemes.Session);
    }

    private static async Task<IResult> Register(RegisterRequest req, HttpContext http, MonitorDb db, ApiConfig config,
        TimeProvider clock, IMailer mailer)
    {
        if (!Http.IsEmail(req.Email)) return Http.Invalid("email", "invalid_email");
        if (!Secrets.PasswordAcceptable(req.Password)) return Http.Invalid("password", "weak_password");
        if (!Http.IsName(req.DisplayName)) return Http.Invalid("displayName", "invalid_name");

        var normalized = SearchText.Email(req.Email!);
        if (await db.Users.AnyAsync(u => u.EmailNormalized == normalized, http.RequestAborted))
        {
            return Results.Accepted();
        }

        var now = clock.GetUtcNow();
        var user = Accounts.NewUser(db, now, req.Email!, req.DisplayName!, verified: false);
        user.PasswordHash = Secrets.HashPassword(user, req.Password!);
        var token = AddUserToken(db, user.Id, TokenPurposes.VerifyEmail, now, config.EmailTokenLifetime);
        Audit.Add(db, http, clock, AuditActions.Register, userId: user.Id);
        await db.SaveChangesAsync(http.RequestAborted);
        await mailer.SendAsync(MailTemplates.Build(MailTemplates.Language(http), "verify", user.Email!,
            Http.Link(config, "/verify-email", token)), http.RequestAborted);
        return Results.Accepted();
    }

    private static async Task<IResult> VerifyEmail(TokenRequest req, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var token = await UseTokenAsync(db, req.Token, TokenPurposes.VerifyEmail, clock, http.RequestAborted);
        if (token is null) return Http.Invalid("token", "invalid_token");

        var user = await db.Users.FirstAsync(u => u.Id == token.UserId, http.RequestAborted);
        user.EmailVerifiedAt ??= clock.GetUtcNow();
        user.UpdatedAt = clock.GetUtcNow();
        Audit.Add(db, http, clock, AuditActions.EmailVerified, userId: user.Id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> Login(LoginRequest req, HttpContext http, MonitorDb db, ApiConfig config, TimeProvider clock)
    {
        var normalized = SearchText.Email(req.Email ?? "");
        var user = await db.Users.FirstOrDefaultAsync(u => u.EmailNormalized == normalized, http.RequestAborted);
        if (user is null || user.Status != UserStatuses.Active || req.Password is null || !Secrets.VerifyPassword(user, req.Password))
        {
            Audit.Add(db, http, clock, AuditActions.SignInFailed, userId: user?.Id);
            await db.SaveChangesAsync(http.RequestAborted);
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "invalid_credentials");
        }

        if (user.EmailVerifiedAt is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "email_not_verified");
        }

        await Http.IssueLoginAsync(http, db, config, clock, user);
        return Results.NoContent();
    }

    private static async Task<IResult> Logout(HttpContext http, MonitorDb db, TimeProvider clock)
    {
        if (http.User.LoginSessionId() is { } id)
        {
            var row = await db.LoginSessions.FirstOrDefaultAsync(s => s.Id == id, http.RequestAborted);
            if (row is not null)
            {
                row.RevokedAt = clock.GetUtcNow();
                Audit.Add(db, http, clock, AuditActions.SignOut, userId: row.UserId);
                await db.SaveChangesAsync(http.RequestAborted);
            }
        }

        http.Response.Cookies.Delete(ApiConfig.SessionCookie);
        return Results.NoContent();
    }

    private static async Task<IResult> Forgot(ForgotRequest req, HttpContext http, MonitorDb db, ApiConfig config,
        TimeProvider clock, IMailer mailer)
    {
        var normalized = SearchText.Email(req.Email ?? "");
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.EmailNormalized == normalized && u.Status == UserStatuses.Active, http.RequestAborted);
        if (user?.Email is not null)
        {
            var token = AddUserToken(db, user.Id, TokenPurposes.ResetPassword, clock.GetUtcNow(), config.EmailTokenLifetime);
            await db.SaveChangesAsync(http.RequestAborted);
            await mailer.SendAsync(MailTemplates.Build(MailTemplates.Language(http), "reset", user.Email,
                Http.Link(config, "/reset-password", token)), http.RequestAborted);
        }

        return Results.Accepted();
    }

    private static async Task<IResult> Reset(ResetRequest req, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        if (!Secrets.PasswordAcceptable(req.Password)) return Http.Invalid("password", "weak_password");
        var token = await UseTokenAsync(db, req.Token, TokenPurposes.ResetPassword, clock, http.RequestAborted);
        if (token is null) return Http.Invalid("token", "invalid_token");

        var now = clock.GetUtcNow();
        var user = await db.Users.FirstAsync(u => u.Id == token.UserId, http.RequestAborted);
        user.PasswordHash = Secrets.HashPassword(user, req.Password!);
        user.EmailVerifiedAt ??= now; // the link proves the mailbox
        user.UpdatedAt = now;
        await db.LoginSessions.Where(s => s.UserId == user.Id && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), http.RequestAborted);
        Audit.Add(db, http, clock, AuditActions.PasswordReset, userId: user.Id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static string AddUserToken(MonitorDb db, Guid userId, string purpose, DateTimeOffset now, TimeSpan lifetime)
    {
        var token = Secrets.NewToken();
        db.UserTokens.Add(new UserToken
        {
            UserId = userId,
            Purpose = purpose,
            TokenHash = Secrets.Hash(token),
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        });
        return token;
    }

    /// <summary>A valid, unused token of the purpose, marked used. Single use: a second call finds nothing.</summary>
    private static async Task<UserToken?> UseTokenAsync(MonitorDb db, string? token, string purpose, TimeProvider clock,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var now = clock.GetUtcNow();
        var hash = Secrets.Hash(token);
        var row = await db.UserTokens.FirstOrDefaultAsync(
            t => t.TokenHash == hash && t.Purpose == purpose && t.UsedAt == null && t.ExpiresAt > now, ct);
        if (row is not null) row.UsedAt = now;
        return row;
    }
}
