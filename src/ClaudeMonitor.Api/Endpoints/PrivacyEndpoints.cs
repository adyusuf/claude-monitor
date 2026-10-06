using System.Text.Json;
using ClaudeMonitor.Api.Background;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Streaming;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

public sealed record DeleteAccountRequest(string? Password, string? Confirm, string? Code = null);

/// <summary>
/// A user's own data (docs/data-model.md, "Account deletion"; GDPR/KVKK): everything about them as one JSON download,
/// and deleting the account. Deletion clears the personal fields and the captured content of the user's sessions,
/// revokes every way in, and keeps only ids and metadata other people's records point at.
/// </summary>
public static class PrivacyEndpoints
{
    public const string ConfirmWord = "DELETE";
    public const string DeletedName = "Deleted user";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/me").RequireAuthorization(Schemes.Session);
        g.MapGet("/export", Export);
        g.MapPost("/delete", Delete).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
    }

    private static async Task Export(HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        var ct = http.RequestAborted;
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        http.Response.ContentType = "application/json";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"claude-monitor-export-{clock.GetUtcNow():yyyy-MM-dd}.json\"";
        // Written into the response's pipe (never a synchronous write) and flushed per chunk, so an export of any size
        // streams without being held in memory.
        var body = http.Response.BodyWriter;
        await using var w = new Utf8JsonWriter(body, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("exportedAt", clock.GetUtcNow());
        Write(w, "user", new { user.Id, user.Email, user.DisplayName, user.CreatedAt, user.EmailVerifiedAt, HasPassword = user.PasswordHash != null });
        Write(w, "signInProviders", await db.UserLogins.AsNoTracking().Where(l => l.UserId == userId)
            .Select(l => new { l.Provider, l.ProviderEmail, l.LinkedAt }).ToListAsync(ct));
        Write(w, "workspaces", await (from m in db.WorkspaceMembers.AsNoTracking()
                                      join ws in db.Workspaces.AsNoTracking() on m.WorkspaceId equals ws.Id
                                      where m.UserId == userId
                                      select new { ws.Id, ws.Name, m.Role, m.JoinedAt, m.RemovedAt }).ToListAsync(ct));
        Write(w, "machines", await (from a in db.Agents.AsNoTracking()
                                    join m in db.Machines.AsNoTracking() on a.MachineId equals m.Id
                                    where a.UserId == userId
                                    select new { AgentId = a.Id, m.Hostname, m.Os, m.Arch, a.Version, a.Status, a.EnrolledAt, a.RevokedAt })
            .ToListAsync(ct));
        Write(w, "commandsSent", await db.SessionCommands.AsNoTracking().Where(c => c.CreatedBy == userId)
            .Select(c => new { c.Id, c.SessionId, c.Kind, c.Body, c.Status, c.CreatedAt }).ToListAsync(ct));
        Write(w, "auditTrail", await db.AuditEvents.AsNoTracking().Where(a => a.ActorUserId == userId).OrderBy(a => a.Id)
            .Select(a => new { a.Action, a.WorkspaceId, a.TargetType, a.TargetId, a.At }).ToListAsync(ct));

        w.WriteStartArray("sessions");
        var sessions = await (from s in db.HarnessSessions.AsNoTracking()
                              join a in db.Agents.AsNoTracking() on s.AgentId equals a.Id
                              where a.UserId == userId
                              orderby s.StartedAt
                              select s).ToListAsync(ct);
        foreach (var s in sessions)
        {
            w.WriteStartObject();
            w.WriteString("id", s.Id);
            w.WriteString("workspaceId", s.WorkspaceId);
            w.WriteString("title", s.Title);
            w.WriteString("model", s.Model);
            w.WriteString("status", s.Status);
            w.WriteString("startedAt", s.StartedAt);
            Write(w, "tasks", await db.SessionTasks.AsNoTracking().Where(t => t.SessionId == s.Id)
                .Select(t => new { t.Subject, t.Status, t.UpdatedAt }).ToListAsync(ct));
            Write(w, "usage", await db.SessionUsage.AsNoTracking().Where(u => u.SessionId == s.Id)
                .Select(u => new { u.Model, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens, u.CostUsd }).ToListAsync(ct));
            w.WriteStartArray("events");
            long after = 0;
            while (true)
            {
                var chunk = await db.SessionEvents.AsNoTracking().Where(e => e.SessionId == s.Id && e.Id > after)
                    .OrderBy(e => e.Id).Take(500).ToListAsync(ct);
                if (chunk.Count == 0) break;
                foreach (var e in chunk)
                {
                    JsonSerializer.Serialize(w, new { e.Kind, e.OccurredAt, e.Truncated, Payload = e.Payload.RootElement }, Json);
                    e.Payload.Dispose();
                }

                after = chunk[^1].Id;
                await w.FlushAsync(ct);
                await body.FlushAsync(ct);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        await PrivacyRemoteData.ExportAsync(w, body, db, userId, ct);
        w.WriteEndObject();
        await w.FlushAsync(ct);
        await body.FlushAsync(ct);
    }

    /// <summary>True when the current login session started within the re-authentication window. Fail-closed: a
    /// session that cannot be found counts as old.</summary>
    internal static async Task<bool> RecentSignInAsync(HttpContext http, MonitorDb db, ApiConfig config, TimeProvider clock)
    {
        if (http.User.LoginSessionId() is not { } id) return false;
        var createdAt = await db.LoginSessions.Where(s => s.Id == id).Select(s => (DateTimeOffset?)s.CreatedAt)
            .FirstOrDefaultAsync(http.RequestAborted);
        return createdAt is { } at && clock.GetUtcNow() - at <= config.ReauthWindow;
    }

    internal static void Write<T>(Utf8JsonWriter w, string name, T value)
    {
        w.WritePropertyName(name);
        JsonSerializer.Serialize(w, value, Json);
    }

    private static async Task<IResult> Delete(DeleteAccountRequest req, HttpContext http, MonitorDb db, ApiConfig config, TimeProvider clock,
        Broker broker)
    {
        var userId = http.User.UserId();
        var ct = http.RequestAborted;
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        if (req.Confirm != ConfirmWord) return Http.Invalid("confirm", "confirm_required");
        if (user.PasswordHash is not null && (req.Password is null || !Secrets.VerifyPassword(user, req.Password)))
        {
            return Http.Invalid("password", "invalid_credentials");
        }

        if (user.TotpEnabledAt is not null && !await MfaEndpoints.CheckAsync(db, config, user, req.Code, clock.GetUtcNow(), ct))
        {
            return Http.Invalid("code", "invalid_code");
        }

        // Neither a password nor a code to ask for (a GitHub/Google-only account): the proof is a fresh sign-in.
        if (user.PasswordHash is null && user.TotpEnabledAt is null && !await RecentSignInAsync(http, db, config, clock))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "reauth_required");
        }

        var memberships = await db.WorkspaceMembers.Where(m => m.UserId == userId && m.RemovedAt == null).ToListAsync(ct);
        var soleOwned = new List<Guid>();
        var solo = new List<Guid>();
        foreach (var m in memberships)
        {
            var others = await db.WorkspaceMembers.CountAsync(x => x.WorkspaceId == m.WorkspaceId && x.UserId != userId && x.RemovedAt == null, ct);
            var otherOwners = await db.WorkspaceMembers.CountAsync(
                x => x.WorkspaceId == m.WorkspaceId && x.UserId != userId && x.RemovedAt == null && x.Role == Roles.Owner, ct);
            if (others == 0) solo.Add(m.WorkspaceId);
            else if (m.Role == Roles.Owner && otherOwners == 0) soleOwned.Add(m.WorkspaceId);
        }

        if (soleOwned.Count > 0)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "sole_owner",
                extensions: new Dictionary<string, object?> { ["workspaces"] = soleOwned });
        }

        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var email = user.EmailNormalized;
        var agentIds = await db.Agents.Where(a => a.UserId == userId).Select(a => a.Id).ToListAsync(ct);
        var sessions = await db.HarnessSessions.Where(s => agentIds.Contains(s.AgentId))
            .Select(s => new { s.Id, s.WorkspaceId, s.StartedAt }).ToListAsync(ct);
        var sessionIds = sessions.Select(s => s.Id).ToList();

        // Captured content of the user's sessions: gone. What others' records point at (ids, times, usage) stays.
        await db.SessionEvents.Where(e => sessionIds.Contains(e.SessionId)).ExecuteDeleteAsync(ct);
        await db.SessionTasks.Where(t => sessionIds.Contains(t.SessionId)).ExecuteDeleteAsync(ct);
        await db.SubagentRuns.Where(r => sessionIds.Contains(r.SessionId))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Description, (string?)null), ct);
        await db.HarnessSessions.Where(s => sessionIds.Contains(s.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, (string?)null).SetProperty(x => x.TitleSearch, ""), ct);
        await db.PermissionRequests.Where(p => sessionIds.Contains(p.SessionId))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ToolInput, JsonDocument.Parse("{}")).SetProperty(p => p.Reason, (string?)null), ct);
        await db.SessionCommands.Where(c => sessionIds.Contains(c.SessionId) || c.CreatedBy == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Body, (string?)null), ct);

        var notices = new List<Action>();
        await PrivacyRemoteData.DeleteAsync(db, broker, userId, agentIds, now, ct, notices);

        // Every way in: closed.
        await db.Agents.Where(a => a.UserId == userId && a.Status == AgentStatuses.Active).ExecuteUpdateAsync(
            s => s.SetProperty(a => a.Status, AgentStatuses.Revoked).SetProperty(a => a.RevokedAt, now).SetProperty(a => a.RevokedBy, userId), ct);
        await db.AgentTokens.Where(t => agentIds.Contains(t.AgentId) && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        await db.LoginSessions.Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        await db.UserTokens.Where(t => t.UserId == userId && t.UsedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        await db.UserLogins.Where(l => l.UserId == userId).ExecuteDeleteAsync(ct);
        await db.UserRecoveryCodes.Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);
        if (email is not null)
        {
            await db.WorkspaceInvitations.Where(i => i.EmailNormalized == email && i.AcceptedAt == null && i.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedAt, now), ct);
        }

        foreach (var m in memberships) m.RemovedAt = now;
        await db.Workspaces.Where(w => solo.Contains(w.Id)).ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, "archived"), ct);

        user.Email = null;
        user.EmailNormalized = null;
        user.DisplayName = DeletedName;
        user.DisplayNameSearch = "";
        user.PasswordHash = null;
        user.TotpSecret = null;
        user.TotpEnabledAt = null;
        user.Status = UserStatuses.Deleted;
        user.UpdatedAt = now;
        Audit.Add(db, http, clock, AuditActions.AccountDeleted, userId: userId,
            detail: new { sessions = sessionIds.Count, agents = agentIds.Count, archivedWorkspaces = solo.Count });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        notices.ForEach(n => n());
        if (sessions.Count > 0)
        {
            // The archived day files hold the same content past retention: it leaves them too.
            await ArchivePurge.RemoveSessionsAsync(db, sessionIds, sessions.Select(s => s.WorkspaceId).Distinct().ToList(),
                DateOnly.FromDateTime(sessions.Min(s => s.StartedAt).UtcDateTime), ct);
        }

        http.Response.Cookies.Delete(ApiConfig.SessionCookie);
        return Results.NoContent();
    }
}
