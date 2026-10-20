using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Integrations;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>
/// Work tracker connections (ADR-0015 §8): who connected which site, each person's own Atlassian account, and the sign-in round trip. Reading
/// needs any workspace role; connecting a site, changing its settings and disconnecting need admin; linking one's own account needs member.
/// </summary>
public static class IntegrationEndpoints
{
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapGet("/workspaces/{ws:guid}/integrations", List);
        g.MapPost("/workspaces/{ws:guid}/integrations/jira/connect", Connect);
        g.MapPost("/integrations/{id:guid}/account/connect", ConnectAccount);
        g.MapGet("/integrations/jira/callback", Callback);
        g.MapPut("/integrations/{id:guid}", Settings);
        g.MapDelete("/integrations/{id:guid}", Disconnect);
    }

    private static async Task<IResult> List(Guid ws, HttpContext http, MonitorDb db, ApiConfig config)
    {
        var me = http.User.UserId();
        if (await Access.MemberAsync(db, me, ws, Roles.Viewer, http.RequestAborted) is null) return Http.NotFound();
        var connections = await db.IntegrationConnections.AsNoTracking()
            .Where(c => c.WorkspaceId == ws && c.Status != ConnectionStatuses.Disconnected).OrderBy(c => c.CreatedAt).ToListAsync(http.RequestAborted);
        var ids = connections.Select(c => c.Id).ToList();
        var accounts = await db.IntegrationAccounts.AsNoTracking().Where(a => ids.Contains(a.ConnectionId) && a.UserId == me)
            .ToDictionaryAsync(a => a.ConnectionId, http.RequestAborted);
        var connectors = connections.Select(c => c.ConnectorUserId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => connectors.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, http.RequestAborted);
        var counts = await db.Projects.AsNoTracking().Where(p => p.SourceConnectionId != null && ids.Contains(p.SourceConnectionId.Value) && p.Status == ManagedStatuses.Active)
            .GroupBy(p => p.SourceConnectionId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, http.RequestAborted);
        var rows = connections.Select(c => IntegrationViews.Of(c, names.GetValueOrDefault(c.ConnectorUserId, ""), me, accounts.GetValueOrDefault(c.Id), counts.GetValueOrDefault(c.Id))).ToList();
        return Results.Ok(new IntegrationsView(config.JiraAvailable, config.JiraAvailable ? [TrackerProviders.Jira] : [], rows));
    }

    private static async Task<IResult> Connect(Guid ws, ConnectRequest req, HttpContext http, MonitorDb db, ApiConfig config, TimeProvider clock, JiraOAuth oauth)
    {
        var me = http.User.UserId();
        if (await Access.MemberAsync(db, me, ws, Roles.Admin, http.RequestAborted) is null) return Http.NotFound();
        if (!config.JiraAvailable) return Http.Invalid("provider", "integration_unavailable");
        if (JiraOAuth.SiteHost(req?.Subdomain) is not { } host) return Http.Invalid("subdomain", "invalid_site");
        if (!await RemoteChecks.ReauthAsync(http, db, config, clock, me, req!.Code)) return RemoteChecks.ReauthRefused();
        return Results.Ok(await StartAsync(db, config, clock, oauth, me, ws, null, OAuthPurposes.Connect, host, http.RequestAborted));
    }

    private static async Task<IResult> ConnectAccount(Guid id, HttpContext http, MonitorDb db, ApiConfig config, TimeProvider clock, JiraOAuth oauth)
    {
        var me = http.User.UserId();
        var connection = await db.IntegrationConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.Status != ConnectionStatuses.Disconnected, http.RequestAborted);
        if (connection is null || await Access.MemberAsync(db, me, connection.WorkspaceId, Roles.Member, http.RequestAborted) is null) return Http.NotFound();
        if (!config.JiraAvailable) return Http.Invalid("provider", "integration_unavailable");
        return Results.Ok(await StartAsync(db, config, clock, oauth, me, connection.WorkspaceId, id, OAuthPurposes.Account, connection.SiteHost, http.RequestAborted));
    }

    private static async Task<ConnectResponse> StartAsync(MonitorDb db, ApiConfig config, TimeProvider clock, JiraOAuth oauth, Guid user, Guid ws,
        Guid? connectionId, string purpose, string host, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        db.IntegrationOAuthStates.RemoveRange(await db.IntegrationOAuthStates.Where(s => s.ExpiresAt < now).ToListAsync(ct));
        var state = Secrets.NewToken();
        var verifier = JiraOAuth.NewVerifier();
        db.IntegrationOAuthStates.Add(new IntegrationOAuthState
        {
            StateHash = Secrets.Hash(state),
            UserId = user,
            WorkspaceId = ws,
            ConnectionId = connectionId,
            Purpose = purpose,
            SiteHost = host,
            Verifier = IntegrationTokens.Seal(config, verifier),
            CreatedAt = now,
            ExpiresAt = now + StateLifetime,
        });
        await db.SaveChangesAsync(ct);
        return new ConnectResponse(oauth.AuthorizeUrl(state, verifier));
    }

    /// <summary>The browser comes back here from Atlassian. Always ends in a redirect to the web page, with the result as a short code.</summary>
    private static async Task<IResult> Callback(string? code, string? state, string? error, HttpContext http, MonitorDb db, ApiConfig config,
        TimeProvider clock, JiraOAuth oauth, IntegrationTokens tokens)
    {
        var me = http.User.UserId();
        var ct = http.RequestAborted;
        var row = state is { Length: > 0 } ? await db.IntegrationOAuthStates.FirstOrDefaultAsync(s => s.StateHash == Secrets.Hash(state), ct) : null;
        if (row is null || row.UserId != me || row.ExpiresAt < clock.GetUtcNow()) return Back(config, null, "invalid_state");
        db.IntegrationOAuthStates.Remove(row); // one use
        await db.SaveChangesAsync(ct);
        if (error is not null || code is not { Length: > 0 }) return Back(config, row.WorkspaceId, "denied");
        try
        {
            var result = await IntegrationConnect.CompleteAsync(db, config, clock, oauth, row, code, http);
            return Back(config, row.WorkspaceId, result);
        }
        catch (TrackerException e)
        {
            return Back(config, row.WorkspaceId, e.Code);
        }
    }

    private static async Task<IResult> Settings(Guid id, ConnectionSettings req, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var me = http.User.UserId();
        var c = await db.IntegrationConnections.FirstOrDefaultAsync(x => x.Id == id && x.Status != ConnectionStatuses.Disconnected, http.RequestAborted);
        if (c is null || await Access.MemberAsync(db, me, c.WorkspaceId, Roles.Admin, http.RequestAborted) is null) return Http.NotFound();
        c.AutoInvite = req.AutoInvite;
        c.UpdatedAt = clock.GetUtcNow();
        Audit.Add(db, http, clock, AuditActions.IntegrationChanged, c.WorkspaceId, me, targetType: "integration", targetId: id, detail: new { autoInvite = req.AutoInvite });
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> Disconnect(Guid id, HttpContext http, MonitorDb db, TimeProvider clock, WebhookManager webhooks)
    {
        var me = http.User.UserId();
        var c = await db.IntegrationConnections.FirstOrDefaultAsync(x => x.Id == id && x.Status != ConnectionStatuses.Disconnected, http.RequestAborted);
        if (c is null || await Access.MemberAsync(db, me, c.WorkspaceId, Roles.Admin, http.RequestAborted) is null) return Http.NotFound();
        await webhooks.TryRemoveAsync(c, http.RequestAborted);
        var now = clock.GetUtcNow();
        c.Status = ConnectionStatuses.Disconnected;
        c.WebhookIds = null;
        c.WebhookExpiresAt = null;
        c.UpdatedAt = now;
        // The tokens go with the connection; the imported rows stay (they keep their source, which now reads as detached).
        db.IntegrationAccounts.RemoveRange(await db.IntegrationAccounts.Where(a => a.ConnectionId == id).ToListAsync(http.RequestAborted));
        Audit.Add(db, http, clock, AuditActions.IntegrationDisconnected, c.WorkspaceId, me, targetType: "integration", targetId: id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static IResult Back(ApiConfig config, Guid? ws, string result) =>
        Results.Redirect($"{config.PublicOrigin}{(ws is { } w ? $"/w/{w}/integrations" : "/")}?result={Uri.EscapeDataString(result)}");
}
