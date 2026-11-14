using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

public sealed record CodeRequest(string? Code);
public sealed record MfaSignInRequest(string? Token, string? Code);
public sealed record MfaSetupResponse(string Secret, string Uri);
public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

/// <summary>How a code asked outside sign-in ended (<see cref="MfaEndpoints.StepUpAsync"/>).</summary>
public enum StepUpOutcome
{
    Accepted,
    Wrong,
    Locked,
}

/// <summary>
/// Two-step sign-in with an authenticator app (TOTP). Set up, confirmed with a first code, then asked at every sign-in,
/// password or provider alike: the first step leaves a short-lived pending token and the second exchanges it with a
/// code (or a single-use recovery code) for the session. Wrong codes count towards the account lock.
/// </summary>
public static class MfaEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var me = api.MapGroup("/me/mfa").RequireAuthorization(Schemes.Session);
        me.MapPost("/setup", Setup);
        me.MapPost("/enable", Enable);
        me.MapPost("/disable", Disable).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        api.MapPost("/auth/mfa", SignIn).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
    }

    /// <summary>For a user with MFA on: the pending token the second step needs (single use, minutes long).</summary>
    public static string Pending(PodiumDb db, ApiConfig config, User user, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(user);
        var token = Secrets.NewToken();
        db.UserTokens.Add(new UserToken
        {
            UserId = user.Id,
            Purpose = TokenPurposes.MfaPending,
            TokenHash = Secrets.Hash(token),
            CreatedAt = now,
            ExpiresAt = now + config.MfaPendingLifetime,
        });
        return token;
    }

    /// <summary>The pending-token cookie: HttpOnly, Strict, scoped to the second step's path, as long as the token lives.</summary>
    public static CookieOptions PendingCookie(ApiConfig config, DateTimeOffset expires)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = config.SecureCookies,
            SameSite = SameSiteMode.Strict,
            Path = ApiConfig.MfaCookiePath,
            Expires = expires,
        };
    }

    /// <summary>True when the code is the current TOTP (not replayed) or an unused recovery code, which is then spent.</summary>
    public static async Task<bool> CheckAsync(PodiumDb db, ApiConfig config, User user, string? code, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(user);
        if (user.TotpEnabledAt is null || user.TotpSecret is null || string.IsNullOrWhiteSpace(code)) return false;
        var step = Totp.Verify(SecretBox.Open(config.MfaKey, user.TotpSecret), code, now, user.TotpLastStep);
        if (step is not null)
        {
            user.TotpLastStep = step;
            return true;
        }

        var hash = Secrets.Hash(RecoveryNormal(code));
        var recovery = await db.UserRecoveryCodes.FirstOrDefaultAsync(r => r.UserId == user.Id && r.CodeHash == hash && r.UsedAt == null, ct);
        if (recovery is null) return false;
        recovery.UsedAt = now;
        return true;
    }

    /// <summary>The audit detail's step of a wrong code asked outside sign-in (sign-in's second step writes "mfa").</summary>
    public const string StepUpStep = "step_up";

    /// <summary>
    /// A code asked outside sign-in (re-authentication, turning two-step sign-in off, deleting the account), counted towards the same lock
    /// as the second step (<see cref="StepUpCounter"/>): the attempt is booked before the code is looked at and refused as locked when it
    /// cannot be; a wrong code is audited and the one that reaches the limit locks. A blank code asks for one and is not counted. An accepted
    /// code starts the count again.
    /// </summary>
    public static async Task<StepUpOutcome> StepUpAsync(HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock, User user, string? code,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(user);
        if (user.TotpEnabledAt is null || string.IsNullOrWhiteSpace(code)) return user.LockedUntil > now ? StepUpOutcome.Locked : StepUpOutcome.Wrong;
        if (!await StepUpCounter.BookAsync(http, config, user.Id, now)) return StepUpOutcome.Locked;
        if (await CheckAsync(db, config, user, code, now, http.RequestAborted))
        {
            await StepUpCounter.ResetAsync(http, user.Id, now);
            return StepUpOutcome.Accepted;
        }

        return await StepUpCounter.MissAsync(http, config, clock, user.Id, now, StepUpStep) ? StepUpOutcome.Locked : StepUpOutcome.Wrong;
    }

    /// <summary>The audit detail's step of a wrong password asked outside sign-in (deleting the account).</summary>
    public const string StepUpPasswordStep = "step_up_password";

    /// <summary>
    /// A password asked outside sign-in, counted like <see cref="StepUpAsync"/>: booked before it is verified, refused as locked when it
    /// cannot be; a wrong one is audited and may lock. A right one gives its booking back and resets nothing (a password alone never undoes
    /// wrong codes). A missing password is not counted.
    /// </summary>
    public static async Task<StepUpOutcome> PasswordStepUpAsync(HttpContext http, ApiConfig config, TimeProvider clock, User user, string? password,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrEmpty(password)) return user.LockedUntil > now ? StepUpOutcome.Locked : StepUpOutcome.Wrong;
        if (!await StepUpCounter.BookAsync(http, config, user.Id, now)) return StepUpOutcome.Locked;
        if (Secrets.VerifyPassword(user, password))
        {
            await StepUpCounter.ReleaseAsync(http, user.Id);
            return StepUpOutcome.Accepted;
        }

        return await StepUpCounter.MissAsync(http, config, clock, user.Id, now, StepUpPasswordStep) ? StepUpOutcome.Locked : StepUpOutcome.Wrong;
    }

    private static string RecoveryNormal(string code) => new string(code.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static async Task<IResult> Setup(HttpContext http, PodiumDb db, ApiConfig config)
    {
        var user = await db.Users.FirstAsync(u => u.Id == http.User.UserId(), http.RequestAborted);
        if (user.TotpEnabledAt is not null) return Results.Conflict();
        var secret = Totp.NewSecret();
        user.TotpSecret = SecretBox.Seal(config.MfaKey, secret);
        user.TotpLastStep = null;
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new MfaSetupResponse(Totp.Base32(secret), Totp.Uri(ApiConfig.MfaIssuer, user.Email ?? user.DisplayName, secret)));
    }

    private static async Task<IResult> Enable(CodeRequest req, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock)
    {
        var user = await db.Users.FirstAsync(u => u.Id == http.User.UserId(), http.RequestAborted);
        if (user.TotpEnabledAt is not null || user.TotpSecret is null) return Results.Conflict();
        var now = clock.GetUtcNow();
        var step = Totp.Verify(SecretBox.Open(config.MfaKey, user.TotpSecret), req.Code, now, null);
        if (step is null) return Http.Invalid("code", "invalid_code");

        user.TotpEnabledAt = now;
        user.TotpLastStep = step;
        await db.UserRecoveryCodes.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(http.RequestAborted);
        var codes = new List<string>();
        for (var i = 0; i < config.RecoveryCodes; i++)
        {
            var raw = Secrets.NewToken()[..10].ToLowerInvariant();
            var code = $"{raw[..5]}-{raw[5..]}";
            codes.Add(code);
            db.UserRecoveryCodes.Add(new UserRecoveryCode { UserId = user.Id, CodeHash = Secrets.Hash(RecoveryNormal(code)), CreatedAt = now });
        }

        Audit.Add(db, http, clock, AuditActions.MfaEnabled, userId: user.Id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new RecoveryCodesResponse(codes));
    }

    private static async Task<IResult> Disable(CodeRequest req, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock)
    {
        var user = await db.Users.FirstAsync(u => u.Id == http.User.UserId(), http.RequestAborted);
        if (user.TotpEnabledAt is null) return Results.Conflict();
        switch (await StepUpAsync(http, db, config, clock, user, req.Code, clock.GetUtcNow()))
        {
            case StepUpOutcome.Accepted:
                break;
            case StepUpOutcome.Locked:
                return AuthEndpoints.Locked();
            default:
                return Http.Invalid("code", "invalid_code");
        }

        user.TotpSecret = null;
        user.TotpEnabledAt = null;
        user.TotpLastStep = null;
        await db.UserRecoveryCodes.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(http.RequestAborted);
        Audit.Add(db, http, clock, AuditActions.MfaDisabled, userId: user.Id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> SignIn(MfaSignInRequest req, HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        // A password sign-in hands the token back in its answer; a provider sign-in left it in the cookie.
        var token = string.IsNullOrEmpty(req.Token) ? http.Request.Cookies[ApiConfig.MfaCookie] : req.Token;
        var hash = Secrets.Hash(token ?? "");
        var pending = await db.UserTokens.FirstOrDefaultAsync(
            t => t.TokenHash == hash && t.Purpose == TokenPurposes.MfaPending && t.UsedAt == null && t.ExpiresAt > now, http.RequestAborted);
        if (pending is null) return Http.Invalid("token", "invalid_token");
        var user = await db.Users.FirstAsync(u => u.Id == pending.UserId, http.RequestAborted);
        if (user.LockedUntil > now) return AuthEndpoints.Locked();
        if (user.Status != UserStatuses.Active) return Http.Invalid("token", "invalid_token");
        if (!await CheckAsync(db, config, user, req.Code, now, http.RequestAborted))
        {
            if (AuthEndpoints.RecordFailure(user, config, now))
            {
                pending.UsedAt = now; // a lock ends this attempt; signing in starts again
                EndCookie(http, config);
                Audit.Add(db, http, clock, AuditActions.AccountLocked, userId: user.Id);
            }

            Audit.Add(db, http, clock, AuditActions.SignInFailed, userId: user.Id, detail: new { step = "mfa" });
            await db.SaveChangesAsync(http.RequestAborted);
            return Http.Invalid("code", "invalid_code");
        }

        pending.UsedAt = now;
        user.FailedSignIns = 0;
        EndCookie(http, config);
        await Http.IssueLoginAsync(http, db, config, clock, user);
        return Results.NoContent();
    }

    private static void EndCookie(HttpContext http, ApiConfig config)
    {
        if (http.Request.Cookies.ContainsKey(ApiConfig.MfaCookie))
        {
            http.Response.Cookies.Delete(ApiConfig.MfaCookie, PendingCookie(config, DateTimeOffset.UnixEpoch));
        }
    }
}
