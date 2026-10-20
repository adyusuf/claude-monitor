using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Integrations;
using ClaudeMonitor.Api.Projects;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Text;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

/// <summary>
/// Listing and importing spaces and sprints, the people of a connection and a pass on demand (ADR-0015 §8). Listing reads Jira live with the
/// connector's account; importing a space needs workspace admin, importing a sprint needs to manage the project, people and passes need admin.
/// </summary>
public static class IntegrationImportEndpoints
{
    public const int ImportMax = 20;

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/integrations/{id:guid}").RequireAuthorization(Schemes.Session);
        g.MapGet("/spaces", Spaces);
        g.MapPost("/spaces/import", ImportSpaces);
        g.MapGet("/spaces/{key}/sprints", Sprints);
        g.MapPost("/spaces/{key}/sprints/import", ImportSprints);
        g.MapGet("/people", People);
        g.MapPost("/sync", Sync);
        g.MapGet("/runs", Runs);
        api.MapPut("/integrations/people/{personId:guid}", PutPerson).RequireAuthorization(Schemes.Session);
    }

    private static async Task<(IntegrationConnection? Connection, WorkspaceMember? Member)> OfAsync(MonitorDb db, HttpContext http, Guid id, string minRole)
    {
        var connection = await db.IntegrationConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.Status != ConnectionStatuses.Disconnected, http.RequestAborted);
        var member = connection is null ? null : await Access.MemberAsync(db, http.User.UserId(), connection.WorkspaceId, minRole, http.RequestAborted);
        return member is null ? (null, null) : (connection, member);
    }

    private static async Task<IResult> Spaces(Guid id, HttpContext http, MonitorDb db, IWorkTracker tracker, IntegrationTokens tokens)
    {
        if ((await OfAsync(db, http, id, Roles.Viewer)).Connection is not { } c) return Http.NotFound();
        if (await tokens.ConnectorContextAsync(c, http.RequestAborted) is not { } ctx) return RemoteReauthorize();
        try
        {
            var spaces = await tracker.ListContainersAsync(ctx, http.RequestAborted);
            var imported = await db.IntegrationLinks.AsNoTracking().Where(l => l.ConnectionId == id && l.Kind == LinkKinds.Project).ToListAsync(http.RequestAborted);
            var byExternal = imported.ToDictionary(l => l.ExternalId, l => l.LocalId);
            return Results.Ok(spaces.Take(500).Select(s => new SpaceView(s.Key, s.Name, s.Description, byExternal.ContainsKey(s.Id), byExternal.TryGetValue(s.Id, out var p) ? p : null)));
        }
        catch (TrackerException e)
        {
            return Failure(e);
        }
    }

    private static async Task<IResult> ImportSpaces(Guid id, ImportSpacesRequest req, HttpContext http, MonitorDb db, ImportService import)
    {
        var (c, _) = await OfAsync(db, http, id, Roles.Admin);
        if (c is null) return Http.NotFound();
        var keys = (req.Keys ?? []).Select(k => k.Trim()).Where(k => k.Length is > 0 and <= 64).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0 || keys.Count > ImportMax) return Http.Invalid("keys", "invalid_list");
        return Results.Ok(await import.ImportSpacesAsync(c, keys, http.User.UserId(), http.RequestAborted));
    }

    private static async Task<IResult> Sprints(Guid id, string key, HttpContext http, MonitorDb db, IWorkTracker tracker, IntegrationTokens tokens)
    {
        if ((await OfAsync(db, http, id, Roles.Viewer)).Connection is not { } c) return Http.NotFound();
        if (!await db.IntegrationLinks.AnyAsync(l => l.ConnectionId == id && l.Kind == LinkKinds.Project && l.ExternalKey == key, http.RequestAborted)) return Http.NotFound();
        if (await tokens.ConnectorContextAsync(c, http.RequestAborted) is not { } ctx) return RemoteReauthorize();
        try
        {
            var found = await tracker.ListIterationsAsync(ctx, key, http.RequestAborted);
            var links = await db.IntegrationLinks.AsNoTracking().Where(l => l.ConnectionId == id && l.Kind == LinkKinds.Sprint).ToDictionaryAsync(l => l.ExternalId, l => l.LocalId, http.RequestAborted);
            return Results.Ok(found.OrderByDescending(s => s.Id.Length).ThenByDescending(s => s.Id, StringComparer.Ordinal)
                .Select(s => new SprintChoice(s.Id, s.Name, JiraMapping.Sprint(s.State), s.Goal, s.Start, s.End, links.ContainsKey(s.Id), links.TryGetValue(s.Id, out var l) ? l : null)));
        }
        catch (TrackerException e)
        {
            return Failure(e);
        }
    }

    private static async Task<IResult> ImportSprints(Guid id, string key, ImportSprintsRequest req, HttpContext http, MonitorDb db, ImportService import)
    {
        var (c, _) = await OfAsync(db, http, id, Roles.Viewer);
        if (c is null) return Http.NotFound();
        var link = await db.IntegrationLinks.AsNoTracking().FirstOrDefaultAsync(l => l.ConnectionId == id && l.Kind == LinkKinds.Project && l.ExternalKey == key, http.RequestAborted);
        if (link is null) return Http.NotFound();
        var grant = await ProjectAccess.OfAsync(db, http.User.UserId(), link.LocalId, http.RequestAborted);
        if (grant is null) return Http.NotFound();
        if (!grant.CanEdit) return Results.Forbid();
        var ids = (req.SprintIds ?? []).Select(s => s.Trim()).Where(s => s.Length is > 0 and <= 20 && s.All(char.IsAsciiDigit)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0 || ids.Count > ImportMax) return Http.Invalid("sprintIds", "invalid_list");
        return Results.Ok(await import.ImportSprintsAsync(c, key, ids, http.RequestAborted));
    }

    private static async Task<IResult> People(Guid id, HttpContext http, MonitorDb db)
    {
        if ((await OfAsync(db, http, id, Roles.Admin)).Connection is null) return Http.NotFound();
        var rows = await (from p in db.IntegrationPeople.AsNoTracking()
                          join u in db.Users.AsNoTracking() on p.UserId equals u.Id into users
                          from u in users.DefaultIfEmpty()
                          where p.ConnectionId == id
                          orderby p.DisplayName
                          select new PersonView(p.Id, p.DisplayName, p.Email, p.UserId, u == null ? null : u.DisplayName, p.InvitedAt != null, p.Active))
            .Take(1000).ToListAsync(http.RequestAborted);
        return Results.Ok(rows);
    }

    private static async Task<IResult> PutPerson(Guid personId, PersonInput req, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        var person = await db.IntegrationPeople.FirstOrDefaultAsync(p => p.Id == personId, http.RequestAborted);
        var connection = person is null ? null : await db.IntegrationConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == person.ConnectionId && c.Status != ConnectionStatuses.Disconnected, http.RequestAborted);
        var me = http.User.UserId();
        if (person is null || connection is null || await Access.MemberAsync(db, me, connection.WorkspaceId, Roles.Admin, http.RequestAborted) is null) return Http.NotFound();
        if (req.UserId is { } user)
        {
            if (await Access.MemberAsync(db, user, connection.WorkspaceId, Roles.Viewer, http.RequestAborted) is null) return Http.Invalid("userId", "not_a_member");
            person.UserId = user;
        }

        if (req.Email is { Length: > 0 } email)
        {
            if (!Http.IsEmail(email)) return Http.Invalid("email", "invalid_email");
            person.Email = email.Trim();
            person.EmailNormalized = SearchText.Email(email);
        }

        Audit.Add(db, http, clock, AuditActions.IntegrationPersonLinked, connection.WorkspaceId, me, targetType: "integration_person", targetId: personId);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> Sync(Guid id, HttpContext http, MonitorDb db, SyncService sync)
    {
        var (c, _) = await OfAsync(db, http, id, Roles.Admin);
        if (c is null) return Http.NotFound();
        var run = await sync.PassAsync(c, http.RequestAborted);
        return Results.Ok(View(run));
    }

    private static async Task<IResult> Runs(Guid id, HttpContext http, MonitorDb db)
    {
        if ((await OfAsync(db, http, id, Roles.Viewer)).Connection is null) return Http.NotFound();
        var rows = await db.IntegrationSyncRuns.AsNoTracking().Where(r => r.ConnectionId == id).OrderByDescending(r => r.StartedAt).Take(10).ToListAsync(http.RequestAborted);
        return Results.Ok(rows.Select(View));
    }

    private static SyncRunView View(IntegrationSyncRun r) => new(r.Id, r.Kind, r.Status, r.Created, r.Updated, r.Archived, r.People, r.Error, r.StartedAt, r.EndedAt);

    private static IResult RemoteReauthorize() => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "reauthorize");

    private static IResult Failure(TrackerException e) => Results.Problem(
        statusCode: e.Code == TrackerException.Codes.Forbidden ? StatusCodes.Status403Forbidden : StatusCodes.Status502BadGateway, title: e.Code);
}
