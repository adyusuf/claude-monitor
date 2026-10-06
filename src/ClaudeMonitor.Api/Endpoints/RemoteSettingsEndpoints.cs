using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

public sealed record RemoteSettings(bool RemoteRunsEnabled, int AlertCpuPct, int AlertMemoryPct, int AlertDiskPct, int AlertSustainSeconds);

public sealed record RemoteSettingsRequest(bool? RemoteRunsEnabled, int? AlertCpuPct, int? AlertMemoryPct, int? AlertDiskPct,
    int? AlertSustainSeconds);

/// <summary>
/// The workspace's remote-work switch and alert thresholds (ADR-0004). Any member reads; an admin changes. Turning the
/// switch off cancels every open run of the workspace at once. Agents pick the new values up with their settings.
/// </summary>
public static class RemoteSettingsEndpoints
{
    public const int SustainMin = 60;
    public const int SustainMax = 86_400;

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/workspaces").RequireAuthorization(Schemes.Session);
        g.MapGet("/{id:guid}/remote-settings", Get);
        g.MapPut("/{id:guid}/remote-settings", Put);
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, MonitorDb db)
    {
        if (await Access.MemberAsync(db, http.User.UserId(), id, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        var s = await db.WorkspaceSettings.AsNoTracking().FirstAsync(x => x.WorkspaceId == id, http.RequestAborted);
        return Results.Ok(View(s));
    }

    private static async Task<IResult> Put(Guid id, RemoteSettingsRequest req, HttpContext http, MonitorDb db, TimeProvider clock,
        Broker broker)
    {
        var userId = http.User.UserId();
        if (await Access.MemberAsync(db, userId, id, Roles.Admin, http.RequestAborted) is null) return Http.NotFound();
        foreach (var (field, value) in new[] { ("alertCpuPct", req.AlertCpuPct), ("alertMemoryPct", req.AlertMemoryPct), ("alertDiskPct", req.AlertDiskPct) })
        {
            if (value is < 1 or > 100) return Http.Invalid(field, "out_of_range");
        }

        if (req.AlertSustainSeconds is < SustainMin or > SustainMax) return Http.Invalid("alertSustainSeconds", "out_of_range");
        var s = await db.WorkspaceSettings.FirstAsync(x => x.WorkspaceId == id, http.RequestAborted);
        var before = View(s);
        var turnedOff = s.RemoteRunsEnabled && req.RemoteRunsEnabled == false;
        s.RemoteRunsEnabled = req.RemoteRunsEnabled ?? s.RemoteRunsEnabled;
        s.AlertCpuPct = req.AlertCpuPct ?? s.AlertCpuPct;
        s.AlertMemoryPct = req.AlertMemoryPct ?? s.AlertMemoryPct;
        s.AlertDiskPct = req.AlertDiskPct ?? s.AlertDiskPct;
        s.AlertSustainSeconds = req.AlertSustainSeconds ?? s.AlertSustainSeconds;
        s.UpdatedAt = clock.GetUtcNow();
        s.UpdatedBy = userId;
        Audit.Add(db, http, clock, AuditActions.RemoteSettingsChanged, id, userId, detail: new { before, after = View(s) });
        await db.SaveChangesAsync(http.RequestAborted);
        if (turnedOff) await RunDecisions.CancelOpenAsync(db, broker, clock.GetUtcNow(), id, null, null, http.RequestAborted);
        return Results.Ok(View(s));
    }

    private static RemoteSettings View(WorkspaceSettings s) =>
        new(s.RemoteRunsEnabled, s.AlertCpuPct, s.AlertMemoryPct, s.AlertDiskPct, s.AlertSustainSeconds);
}
