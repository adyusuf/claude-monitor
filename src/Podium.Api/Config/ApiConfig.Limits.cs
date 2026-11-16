using Podium.Contracts;
namespace Podium.Api.Config;

/// <summary>
/// The limits and lifetimes of remote work, asks, metrics and streams: the second half of <see cref="ApiConfig"/>, moved here unchanged
/// so the main file stays under 300 lines (global #9). Same rules as there: the one configuration module, no other file holds a value.
/// </summary>
public sealed partial record ApiConfig
{
    /// <summary>Remote work (ADR-0005): how long a run waits for its owner, and how many may wait per target.</summary>
    public TimeSpan RunPendingLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public int RunPendingMaxPerTarget { get; init; } = 10;

    /// <summary>An approved run the target has not started within this long is expired (it carries this as not_after).</summary>
    public TimeSpan RunStartWindow { get; init; } = TimeSpan.FromMinutes(15);
    public long RunOutputMax { get; init; } = 1024 * 1024;
    public int RunChunkMax { get; init; } = 64 * 1024;
    public int RunTextMax { get; init; } = 8192;

    /// <summary>The longest script body (ADR-0008 §3c); RunTextMax keeps applying to argv elements and shell text. At most <see cref="ScriptInterpreters.BodyCeiling"/>.</summary>
    public int RunScriptMax { get; init; } = ScriptInterpreters.BodyMax;
    public long ScriptOutputFloor { get; init; } = 1024 * 1024;
    public long ScriptOutputDefault { get; init; } = 8 * 1024 * 1024;
    public long ScriptOutputCeiling { get; init; } = 64 * 1024 * 1024;

    // Asks across machines (ADR-0008 §3f).
    public int AskQuestionMax { get; init; } = 8000;
    public int AskAnswerMax { get; init; } = 100_000;
    // An ask the target's daemon holds waits this long for an idle Claude session there (it used to be 2 minutes, when it was refused at once without one).
    public TimeSpan AskQueuedLifetime { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan AskAnswerWait { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan AskDepthWindow { get; init; } = TimeSpan.FromMinutes(15);
    public int AskPerSessionPer10Min { get; init; } = 5;
    public int AskPerAgentPerHour { get; init; } = 30;
    public int RunArgvMax { get; init; } = 64;
    public int RunReasonMax { get; init; } = 500;

    /// <summary>Runs a requester may create per minute (approval spam is a denial of service on the owner).</summary>
    public int RunCreatesPerMinute { get; init; } = 20;
    public int GrantDaysMax { get; init; } = 90;
    public TimeSpan MetricsRetention { get; init; } = TimeSpan.FromDays(7);
    public int MetricsReportMax { get; init; } = 60;

    /// <summary>How old a sample may be when it arrives (an agent keeps samples while offline); older ones are dropped.</summary>
    public TimeSpan MetricsReportWindow { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The most metric points one request returns (one day at one a minute).</summary>
    public int MetricsPointsMax { get; init; } = 1440;

    /// <summary>An agent whose heartbeat is older than this is shown offline.</summary>
    public TimeSpan OnlineWindow { get; init; } = TimeSpan.FromMinutes(3);
    public int MetricsDisksMax { get; init; } = 16;

    /// <summary>A service agent silent this long gets an "offline" alert.</summary>
    public TimeSpan OfflineAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How far an agent's clock may differ from the API's before a time it reports is not believed.</summary>
    public TimeSpan ClockSkewMax { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How much of Claude's answer a command carries, and how many recorded messages are searched for it.</summary>
    public int ReplyPreviewChars { get; init; } = 300;
    public int ReplyScanMax { get; init; } = 500;

    /// <summary>How often an idle live stream sends a ping; the workspace stream also re-checks its reader's access then.</summary>
    public TimeSpan StreamPingEvery { get; init; } = TimeSpan.FromSeconds(20);
}
