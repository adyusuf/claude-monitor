using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Mail;
using Podium.Api.Security;
using Podium.Api.Text;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

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
        var auth = api.MapGroup("/auth").RequireRateLimiting(RateLimitPolicy).AllowAnonymous();
        auth.MapPost("/register", Register);
        auth.MapPost("/verify-email", VerifyEmail);
        auth.MapPost("/login", Login);
        auth.MapPost("/password/forgot", Forgot);
        auth.MapPost("/password/reset", Reset);
        api.MapPost("/auth/logout", Logout).RequireAuthorization(Schemes.Session);
    }

    private static async Task<IResult> Register(RegisterRequest req, HttpContext http, PodiumDb db, ApiConfig config,
        TimeProvider clock, IMailer mailer, ILoggerFactory logs)
    {
        if (!Http.IsEmail(req.Email)) return Http.Invalid("email", "invalid_email");
        if (!Secrets.PasswordAcceptable(req.Password)) return Http.Invalid("password", "weak_password");
        if (!Http.IsName(req.DisplayName)) return Http.Invalid("displayName", "invalid_name");

        var normalized = SearchText.Email(req.Email!);
        if (await db.Users.AnyAsync(u => u.EmailNormalized == normalized, http.RequestAborted))
        {
            Secrets.HashPassword(new User(), req.Password!); // the hash a new account costs, so timing does not tell a registered address apart
            return Results.Accepted();
        }

        var now = clock.GetUtcNow();
        var user = Accounts.NewUser(db, now, req.Email!, req.DisplayName!, verified: false);
        user.PasswordHash = Secrets.HashPassword(user, req.Password!);
        user.Language = MailTemplates.Language(http); // the language they registered in: later mails to them follow it
        var token = AddUserToken(db, user.Id, TokenPurposes.VerifyEmail, now, config.EmailTokenLifetime);
        Audit.Add(db, http, clock, AuditActions.Register, userId: user.Id);
        await db.SaveChangesAsync(http.RequestAborted);
        SendOffRequest(mailer, MailTemplates.Build(MailTemplates.Language(http), "verify", user.Email!,
            Http.Link(config, "/verify-email", token)), logs);
        return Results.Accepted();
    }

    private static async Task<IResult> VerifyEmail(TokenRequest req, HttpContext http, PodiumDb db, TimeProvider clock)
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

    private static async Task<IResult> Login(LoginRequest req, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock)
    {
        var normalized = SearchText.Email(req.Email ?? "");
        var user = await db.Users.FirstOrDefaultAsync(u => u.EmailNormalized == normalized, http.RequestAborted);
        var now = clock.GetUtcNow();
        // One verification on every path (unknown address, no password, locked) so timing tells nothing apart.
        var passwordOk = Secrets.VerifyPasswordOrDummy(user, req.Password);
        // Locked: even the right password waits and gets the same answer as a wrong one, so the lock can neither test
        // passwords nor reveal that the address is registered. Attempts during the lock neither count nor extend it.
        var locked = user?.LockedUntil > now;
        if (locked || user is null || user.Status != UserStatuses.Active || !passwordOk)
        {
            if (user is not null && !locked && RecordFailure(user, config, now))
            {
                Audit.Add(db, http, clock, AuditActions.AccountLocked, userId: user.Id);
            }

            Audit.Add(db, http, clock, AuditActions.SignInFailed, userId: user?.Id, detail: locked ? new { locked } : null);
            await db.SaveChangesAsync(http.RequestAborted);
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "invalid_credentials");
        }

        if (user.EmailVerifiedAt is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "email_not_verified");
        }

        user.LockedUntil = null;
        if (user.TotpEnabledAt is not null)
        {
            // The count is reset by the finished second step only (MfaEndpoints.SignIn): a fresh password must not undo the wrong codes.
            var token = MfaEndpoints.Pending(db, config, user, now);
            await db.SaveChangesAsync(http.RequestAborted);
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "mfa_required",
                extensions: new Dictionary<string, object?> { ["mfaToken"] = token });
        }

        user.FailedSignIns = 0;
        await Http.IssueLoginAsync(http, db, config, clock, user);
        return Results.NoContent();
    }

    /// <summary>Counts a failed attempt; returns true when this one locked the account.</summary>
    public static bool RecordFailure(User user, ApiConfig config, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(config);
        user.FailedSignIns++;
        if (user.FailedSignIns < config.LockoutAfter) return false;
        user.FailedSignIns = 0;
        user.LockedUntil = now + config.LockoutFor;
        return true;
    }

    public static IResult Locked() =>
        Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "account_locked");

    private static async Task<IResult> Logout(HttpContext http, PodiumDb db, TimeProvider clock)
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

    private static async Task<IResult> Forgot(ForgotRequest req, HttpContext http, PodiumDb db, ApiConfig config,
        TimeProvider clock, IMailer mailer, ILoggerFactory logs)
    {
        var normalized = SearchText.Email(req.Email ?? "");
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.EmailNormalized == normalized && u.Status == UserStatuses.Active, http.RequestAborted);
        var since = clock.GetUtcNow() - config.ResetMailEvery;
        // One mail per address per window, whatever the number of callers or their addresses: the link already mailed keeps working.
        if (user?.Email is not null && !await db.UserTokens.AnyAsync(
                t => t.UserId == user.Id && t.Purpose == TokenPurposes.ResetPassword && t.CreatedAt > since, http.RequestAborted))
        {
            await EndResetLinksAsync(db, user.Id, clock.GetUtcNow(), http.RequestAborted);
            var token = AddUserToken(db, user.Id, TokenPurposes.ResetPassword, clock.GetUtcNow(), config.EmailTokenLifetime);
            await db.SaveChangesAsync(http.RequestAborted);
            SendOffRequest(mailer, MailTemplates.Build(MailTemplates.Language(http, user.Language), "reset", user.Email,
                Http.Link(config, "/reset-password", token)), logs);
        }

        return Results.Accepted();
    }

    private static async Task<IResult> Reset(ResetRequest req, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        if (!Secrets.PasswordAcceptable(req.Password)) return Http.Invalid("password", "weak_password");
        var token = await UseTokenAsync(db, req.Token, TokenPurposes.ResetPassword, clock, http.RequestAborted);
        if (token is null) return Http.Invalid("token", "invalid_token");

        var now = clock.GetUtcNow();
        var user = await db.Users.FirstAsync(u => u.Id == token.UserId, http.RequestAborted);
        user.PasswordHash = Secrets.HashPassword(user, req.Password!);
        user.EmailVerifiedAt ??= now; // the link proves the mailbox
        // ...and so it also opens a lock: whoever locked the account by guessing cannot keep its owner out.
        user.FailedSignIns = 0;
        user.LockedUntil = null;
        user.UpdatedAt = now;
        await db.LoginSessions.Where(s => s.UserId == user.Id && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), http.RequestAborted);
        await EndResetLinksAsync(db, user.Id, now, http.RequestAborted);
        Audit.Add(db, http, clock, AuditActions.PasswordReset, userId: user.Id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    /// <summary>
    /// Sends an account mail without holding the answer: a known address then costs what an unknown one does (no mail-server time to
    /// measure), and a mail server that fails cannot turn a known address into a 500 where an unknown one gets 202. The send outlives the
    /// request (not its token); a failure is logged with the subject only (the address is personal data).
    /// </summary>
    private static void SendOffRequest(IMailer mailer, MailMessageData message, ILoggerFactory logs) =>
        _ = SendLoggedAsync(mailer, message, logs.CreateLogger(typeof(AuthEndpoints)));

    private static async Task SendLoggedAsync(IMailer mailer, MailMessageData message, ILogger log)
    {
        try
        {
            await mailer.SendAsync(message, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError("Account mail not sent: {Subject} ({Type})", message.Subject, e.GetType().Name);
        }
    }

    /// <summary>Ends every open reset link of the user: only the newest one works, and none outlives a changed password.</summary>
    private static Task<int> EndResetLinksAsync(PodiumDb db, Guid userId, DateTimeOffset now, CancellationToken ct) =>
        db.UserTokens.Where(t => t.UserId == userId && t.Purpose == TokenPurposes.ResetPassword && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);

    private static string AddUserToken(PodiumDb db, Guid userId, string purpose, DateTimeOffset now, TimeSpan lifetime)
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

    /// <summary>
    /// A valid, unused token of the purpose, marked used. Single use: a second call finds nothing, and of two calls that both read it
    /// unused only one spends it (compare-and-set on used_at).
    /// </summary>
    private static async Task<UserToken?> UseTokenAsync(PodiumDb db, string? token, string purpose, TimeProvider clock,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var now = clock.GetUtcNow();
        var hash = Secrets.Hash(token);
        var row = await db.UserTokens.AsNoTracking().FirstOrDefaultAsync(
            t => t.TokenHash == hash && t.Purpose == purpose && t.UsedAt == null && t.ExpiresAt > now, ct);
        if (row is null) return null;
        var spent = await db.UserTokens.Where(t => t.Id == row.Id && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        if (spent == 0) return null;
        row.UsedAt = now;
        return row;
    }
}
