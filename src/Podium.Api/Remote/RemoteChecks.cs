using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Security;
using Podium.Api.Streaming;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Remote;

/// <summary>
/// The checks grants and jobs share (ADR-0005): the re-authentication an owner's approval needs, the template check
/// answered as a 400 with the matcher's code, and the compare-and-set cancel of the runs a revoked grant or retired job
/// had approved.
/// </summary>
public static class RemoteChecks
{
    public const string ReauthRequired = "reauth_required";
    public const string InvalidReason = "invalid_reason";

    private static readonly string[] Open =
        [RunStatuses.PendingApproval, RunStatuses.Approved, RunStatuses.Delivered, RunStatuses.Running];

    /// <summary>
    /// True after a sign-in within the re-authentication window, after a two-step code accepted on this sign-in within the code window, or when
    /// the user has two-step sign-in on and sent a valid code (which opens the code window again).
    /// </summary>
    /// <remarks>
    /// With <paramref name="window"/> false the code window does not count (a recent sign-in or a fresh code, as before ADR-0008 §9.13a): for
    /// what reaches as far as a strict approval, such as lowering a strict role, the server's mail and sign-in settings, and an approval key.
    /// </remarks>
    public static async Task<bool> ReauthAsync(HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock, Guid userId, string? code,
        bool window = true)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        if (await PrivacyEndpoints.RecentSignInAsync(http, db, config, clock)) return true;
        var user = await db.Users.FirstAsync(u => u.Id == userId, http.RequestAborted);
        if (user.TotpEnabledAt is null) return false;
        if (window && await RecentCodeAsync(http, db, config, clock)) return true;
        return await CodeAsync(http, db, config, user, code, clock.GetUtcNow());
    }

    /// <summary>
    /// True when a two-step code was accepted on this sign-in (the browser's login session, never an agent token) within the code window
    /// (ADR-0008 §9.13a). A time ahead of the clock does not count.
    /// </summary>
    public static async Task<bool> RecentCodeAsync(HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clock);
        if (http.User.LoginSessionId() is not { } id) return false;
        var at = await db.LoginSessions.Where(s => s.Id == id && s.RevokedAt == null).Select(s => s.CodeCheckedAt).FirstOrDefaultAsync(http.RequestAborted);
        var now = clock.GetUtcNow();
        return at is { } a && a <= now && now - a <= config.CodeWindow;
    }

    /// <summary>
    /// Checks a two-step code (or a recovery code); when it is accepted, this sign-in's code window starts again. The stamp is set on the tracked
    /// login session, so it is written by the caller's save together with the used code's step or recovery code: a request that ends before
    /// saving uses up nothing and opens no window.
    /// </summary>
    public static async Task<bool> CodeAsync(HttpContext http, PodiumDb db, ApiConfig config, User user, string? code, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        if (!await MfaEndpoints.CheckAsync(db, config, user, code, now, http.RequestAborted)) return false;
        if (http.User.LoginSessionId() is { } id
            && await db.LoginSessions.FirstOrDefaultAsync(s => s.Id == id && s.RevokedAt == null, http.RequestAborted) is { } session)
        {
            session.CodeCheckedAt = now;
        }

        return true;
    }

    /// <summary>
    /// The gate of an owner's grant or job approval (and of creating a grant for another user): on a strict target (production or database) a fresh
    /// two-step code is always needed, neither a recent sign-in nor the code window counts, and no two-step sign-in means no approval
    /// (<c>totp_required</c>); elsewhere the re-authentication rule above. Null when the approval may go on (decision §9.13).
    /// </summary>
    public static async Task<IResult?> ApprovalGateAsync(HttpContext http, PodiumDb db, ApiConfig config, TimeProvider clock, Guid userId, Agent target, string? code)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(target);
        var strict = MachineRoles.IsStrict(MachineRoles.EffectiveEnvironment(target.RoleEnvironment, target.ClaimedEnvironment),
            MachineRoles.EffectiveFunction(target.RoleFunction, target.ClaimedFunction));
        if (!strict) return await ReauthAsync(http, db, config, clock, userId, code) ? null : ReauthRefused();
        var user = await db.Users.FirstAsync(u => u.Id == userId, http.RequestAborted);
        if (user.TotpEnabledAt is null) return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: RemoteErrors.TotpRequired);
        return await CodeAsync(http, db, config, user, code, clock.GetUtcNow()) ? null : ReauthRefused();
    }

    public static IResult ReauthRefused() => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: ReauthRequired);

    public static IResult Conflict(string error) => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: error);

    /// <summary>Null when the template is valid for the target's OS, else a 400 whose detail is the matcher's error code.</summary>
    public static IResult? TemplateProblem(IReadOnlyList<string>? argv, string? cwd, int timeoutSeconds, string os, bool requestedByClaude)
    {
        var code = GrantMatcher.Validate(new GrantTemplate(argv!, cwd!, timeoutSeconds), os, requestedByClaude);
        return code is null ? null : Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: RemoteErrors.BadTemplate, detail: code);
    }

    public static IResult? ReasonProblem(string? reason, ApiConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return reason is { } r && (r.Length > config.RunReasonMax || r.Contains('\0')) ? Http.Invalid("reason", InvalidReason) : null;
    }

    /// <summary>Cancels the open runs a filter selects, each by compare-and-set, and tells the streams; the target hears it unless the run was still waiting.</summary>
    public static async Task CancelRunsAsync(PodiumDb db, Broker broker, System.Linq.Expressions.Expression<Func<RemoteRun, bool>> filter,
        DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(broker);
        var runs = await db.RemoteRuns.AsNoTracking().Where(filter).Where(r => Open.Contains(r.Status)).OrderBy(r => r.Id).ToListAsync(ct);
        foreach (var run in runs)
        {
            var changed = await db.RemoteRuns.Where(r => r.Id == run.Id && r.Status == run.Status)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatuses.Cancelled).SetProperty(r => r.EndedAt, now), ct);
            if (changed != 1) continue;
            var was = run.Status;
            run.Status = RunStatuses.Cancelled;
            RunNotices.Changed(broker, run);
            if (was != RunStatuses.PendingApproval) RunNotices.Cancel(broker, run);
        }
    }
}
