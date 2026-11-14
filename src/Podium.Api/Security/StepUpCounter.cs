using Podium.Api.Config;
using Podium.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Security;

/// <summary>
/// The account-lock count of proofs asked outside sign-in (step-up codes), on the same FailedSignIns / LockedUntil as sign-in. An attempt
/// is booked before its proof is looked at, in one statement that refuses once the account is locked or the limit is booked, so attempts
/// sent at once never get more than the limit checked. Every write is a statement of its own in a unit of work of its own: the caller often
/// refuses without saving, and nothing else it tracks may be written with the count.
/// </summary>
public static class StepUpCounter
{
    /// <summary>Books one attempt; false when the account is locked or the limit is already booked (refuse without checking the proof).</summary>
    public static async Task<bool> BookAsync(HttpContext http, ApiConfig config, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(config);
        var limit = config.LockoutAfter;
        await using var scope = http.RequestServices.CreateAsyncScope();
        var own = scope.ServiceProvider.GetRequiredService<PodiumDb>();
        return await own.Users.Where(u => u.Id == userId && (u.LockedUntil == null || u.LockedUntil <= now) && u.FailedSignIns < limit)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.FailedSignIns, u => u.FailedSignIns + 1), http.RequestAborted) == 1;
    }

    /// <summary>
    /// A booked attempt failed: audited, and the account is locked when the booked count reached the limit; true when this one locked it.
    /// </summary>
    public static async Task<bool> MissAsync(HttpContext http, ApiConfig config, TimeProvider clock, Guid userId, DateTimeOffset now, string step)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(config);
        var ct = http.RequestAborted;
        var limit = config.LockoutAfter;
        var until = now + config.LockoutFor;
        await using var scope = http.RequestServices.CreateAsyncScope();
        var own = scope.ServiceProvider.GetRequiredService<PodiumDb>();
        var locked = await own.Users.Where(u => u.Id == userId && u.FailedSignIns >= limit)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.FailedSignIns, 0).SetProperty(u => u.LockedUntil, until), ct) == 1;
        if (locked) Audit.Add(own, http, clock, AuditActions.AccountLocked, userId: userId);
        Audit.Add(own, http, clock, AuditActions.SignInFailed, userId: userId, detail: new { step });
        await own.SaveChangesAsync(ct);
        return locked;
    }

    /// <summary>
    /// An accepted first factor (a password) gives its booking back and resets nothing, so a known password cannot undo wrong codes.
    /// </summary>
    public static async Task ReleaseAsync(HttpContext http, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(http);
        await using var scope = http.RequestServices.CreateAsyncScope();
        var own = scope.ServiceProvider.GetRequiredService<PodiumDb>();
        await own.Users.Where(u => u.Id == userId && u.FailedSignIns > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.FailedSignIns, u => u.FailedSignIns - 1), http.RequestAborted);
    }

    /// <summary>An accepted code: the count starts again (as a finished sign-in does), written now; a lock set meanwhile stays.</summary>
    public static async Task ResetAsync(HttpContext http, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(http);
        await using var scope = http.RequestServices.CreateAsyncScope();
        var own = scope.ServiceProvider.GetRequiredService<PodiumDb>();
        await own.Users.Where(u => u.Id == userId && (u.LockedUntil == null || u.LockedUntil <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.FailedSignIns, 0), http.RequestAborted);
    }
}
