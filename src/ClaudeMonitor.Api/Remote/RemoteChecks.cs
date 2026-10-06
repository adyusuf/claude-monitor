using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Endpoints;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Remote;

/// <summary>
/// The checks grants and jobs share (ADR-0004): the re-authentication an owner's approval needs, the template check
/// answered as a 400 with the matcher's code, and the compare-and-set cancel of the runs a revoked grant or retired job
/// had approved.
/// </summary>
public static class RemoteChecks
{
    public const string ReauthRequired = "reauth_required";
    public const string InvalidReason = "invalid_reason";

    private static readonly string[] Open =
        [RunStatuses.PendingApproval, RunStatuses.Approved, RunStatuses.Delivered, RunStatuses.Running];

    /// <summary>True after a sign-in within the re-authentication window, or when the user has two-step sign-in on and sent a valid code.</summary>
    public static async Task<bool> ReauthAsync(HttpContext http, MonitorDb db, ApiConfig config, TimeProvider clock, Guid userId, string? code)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        if (await PrivacyEndpoints.RecentSignInAsync(http, db, config, clock)) return true;
        var user = await db.Users.FirstAsync(u => u.Id == userId, http.RequestAborted);
        return user.TotpEnabledAt is not null && await MfaEndpoints.CheckAsync(db, config, user, code, clock.GetUtcNow(), http.RequestAborted);
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
    public static async Task CancelRunsAsync(MonitorDb db, Broker broker, System.Linq.Expressions.Expression<Func<RemoteRun, bool>> filter,
        DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(broker);
        var runs = await db.RemoteRuns.AsNoTracking().Where(filter).Where(r => Open.Contains(r.Status)).ToListAsync(ct);
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
