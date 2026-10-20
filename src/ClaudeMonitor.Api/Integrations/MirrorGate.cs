using ClaudeMonitor.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Integrations;

/// <summary>A connection that is ready to be written to, with the acting person's own account.</summary>
public sealed record WriteGate(IntegrationConnection Connection, TrackerContext Ctx);

/// <summary>
/// The first steps of every write to the tool (ADR-0015 §7): the connection must be active and the person must have linked their own account.
/// Failures become the short answers the web translates; nothing here changes a Podium row.
/// </summary>
public sealed class MirrorGate(MonitorDb db, IntegrationTokens tokens)
{
    public const string AccountRequired = "jira_account_required";
    public const string Changed = "jira_changed";
    public const string TransitionUnavailable = "jira_transition_unavailable";
    public const string NoBoard = "jira_no_board";
    public const string Scope = "mirrored_scope";
    public const string TypeLocked = "mirrored_type_locked";
    public const string NoArchive = "mirrored_no_archive";
    public const string FieldLocked = "mirrored_field_locked";

    public async Task<(WriteGate? Gate, IResult? Refusal)> OpenAsync(Guid connectionId, Guid userId, CancellationToken ct)
    {
        var connection = await db.IntegrationConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        if (connection is null || connection.Status == ConnectionStatuses.Disconnected) return (null, Problem(StatusCodes.Status409Conflict, "mirrored_detached"));
        if (connection.Status != ConnectionStatuses.Active) return (null, Problem(StatusCodes.Status409Conflict, "reauthorize"));
        var ctx = await tokens.ContextForAsync(connection, userId, ct);
        return ctx is null ? (null, Problem(StatusCodes.Status409Conflict, AccountRequired)) : (new WriteGate(connection, ctx), null);
    }

    public static IResult Problem(int status, string title) => Results.Problem(statusCode: status, title: title);

    /// <summary>The answer for a failed call to the tool.</summary>
    public static IResult Refusal(TrackerException e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.Code switch
        {
            TrackerException.Codes.Forbidden => Problem(StatusCodes.Status403Forbidden, e.Code),
            TrackerException.Codes.Unauthorized => Problem(StatusCodes.Status409Conflict, AccountRequired),
            TrackerException.Codes.Changed => Problem(StatusCodes.Status409Conflict, Changed),
            TrackerException.Codes.Rejected or TrackerException.Codes.NotFound => Problem(StatusCodes.Status400BadRequest, e.Code),
            TrackerException.Codes.RateLimited => Problem(StatusCodes.Status429TooManyRequests, e.Code),
            _ => Problem(StatusCodes.Status502BadGateway, TrackerException.Codes.Unavailable),
        };
    }

    public Task<IntegrationLink?> LinkOfAsync(string kind, Guid localId, CancellationToken ct) =>
        db.IntegrationLinks.FirstOrDefaultAsync(l => l.Kind == kind && l.LocalId == localId, ct);

    public Task<string?> ProjectKeyAsync(Guid projectId, CancellationToken ct) =>
        db.IntegrationLinks.AsNoTracking().Where(l => l.Kind == LinkKinds.Project && l.LocalId == projectId).Select(l => l.ExternalKey).FirstOrDefaultAsync(ct);

    /// <summary>The tool's account id for a Podium user: their own linked account first, else the person row matched to them.</summary>
    public async Task<string?> AccountOfAsync(Guid connectionId, Guid userId, CancellationToken ct) =>
        await db.IntegrationAccounts.AsNoTracking().Where(a => a.ConnectionId == connectionId && a.UserId == userId).Select(a => a.ExternalAccountId).FirstOrDefaultAsync(ct)
        ?? await db.IntegrationPeople.AsNoTracking().Where(p => p.ConnectionId == connectionId && p.UserId == userId).Select(p => p.ExternalAccountId).FirstOrDefaultAsync(ct);
}
