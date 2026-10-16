namespace ClaudeMonitor.Contracts;

/// <summary>How a schedule repeats (ADR-0010 §3). No cron: an interval, or once a day at a UTC time.</summary>
public static class ScheduleKinds
{
    public const string Interval = "interval";
    public const string Daily = "daily";
    public static readonly IReadOnlyList<string> All = [Interval, Daily];
}

public static class ScheduleStatuses
{
    public const string Active = "active";

    /// <summary>Stopped, and the owner can start it again: by the owner, or by the API after repeated failures or because the target became strict.</summary>
    public const string Paused = "paused";

    /// <summary>Over for good: its job was retired, its owner or its target went away, or the owner ended it.</summary>
    public const string Ended = "ended";
    public static readonly IReadOnlyList<string> All = [Active, Paused, Ended];
}

/// <summary>Why a schedule is paused or ended, as a stable code the web turns into text.</summary>
public static class ScheduleReasons
{
    public const string Owner = "owner";
    public const string Failures = "failures";
    public const string Strict = "strict";
    public const string ExecLevel = "exec_level";
    public const string JobRetired = "job_retired";
    public const string Disabled = "disabled";
    public const string TargetGone = "target_gone";

    /// <summary>The machine requires signed approvals: nothing runs there without a person signing it, so nothing runs on a schedule.</summary>
    public const string Approvals = "approvals";
}

/// <summary>The limits of a schedule, one definition for the web and the API.</summary>
public static class ScheduleLimits
{
    public const int IntervalMinMinutes = 15;
    public const int IntervalMaxMinutes = 7 * 24 * 60;
    public const int PerTargetMax = 5;

    /// <summary>Failed, timed-out or expired runs in a row after which a schedule pauses itself.</summary>
    public const int FailuresToPause = 3;

    /// <summary>The next moment a schedule fires after <paramref name="after"/>; null for a shape that is not valid.</summary>
    public static DateTimeOffset? Next(string? kind, int? intervalMinutes, int? dailyAtMinutes, DateTimeOffset after)
    {
        if (!Valid(kind, intervalMinutes, dailyAtMinutes)) return null;
        var minute = new DateTimeOffset(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, TimeSpan.Zero);
        if (kind == ScheduleKinds.Interval) return minute.AddMinutes(intervalMinutes!.Value);
        var today = new DateTimeOffset(after.Year, after.Month, after.Day, 0, 0, 0, TimeSpan.Zero).AddMinutes(dailyAtMinutes!.Value);
        return today > after ? today : today.AddDays(1);
    }

    /// <summary>True for an interval of 15 minutes to 7 days with no daily time, or a daily time of 0 to 1439 minutes after midnight UTC with no interval.</summary>
    public static bool Valid(string? kind, int? intervalMinutes, int? dailyAtMinutes) => kind switch
    {
        ScheduleKinds.Interval => intervalMinutes is >= IntervalMinMinutes and <= IntervalMaxMinutes && dailyAtMinutes is null,
        ScheduleKinds.Daily => dailyAtMinutes is >= 0 and <= 1439 && intervalMinutes is null,
        _ => false,
    };
}

/// <summary>An owner puts an active job on a schedule (ADR-0010 §3). Code is a TOTP or recovery code, needed when the sign-in is not recent.</summary>
public sealed record ScheduleCreate(Guid JobId, string Kind, int? IntervalMinutes, int? DailyAtMinutes, string? Code = null);

/// <summary>The owner's confirmation to start a paused schedule again.</summary>
public sealed record ScheduleResume(string? Code = null);

public sealed record ScheduleView(Guid Id, Guid JobId, string JobName, Guid TargetAgentId, string Kind, int? IntervalMinutes, int? DailyAtMinutes, string Status,
    string? PauseReason, DateTimeOffset? NextRunAt, DateTimeOffset? LastRunAt, string? LastRunStatus, int FailuresInARow, Guid OwnerUserId, bool IsOwner, DateTimeOffset CreatedAt);
