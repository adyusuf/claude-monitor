using System.Buffers.Text;
using System.Text;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>A page of a list that takes a limit and a cursor: the rows, and the cursor of the next page (null on the last one).</summary>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>The workspace's member list (GET /workspaces/{id}/members).</summary>
public static class WorkspaceMemberList
{
    /// <summary>What a list asked for without a limit or a cursor (as before paging existed) holds at most: the first of this many.</summary>
    public const int ListMax = 500;

    private const char CursorSeparator = '\n';

    /// <summary>
    /// Any member reads it. Admins and owners see each member's e-mail address; viewers and members get null in its place (F-225): the
    /// page and the pickers need names and roles, not other people's addresses. With neither <paramref name="limit"/> nor
    /// <paramref name="cursor"/> the answer is the plain array it always was, capped at <see cref="ListMax"/>; with either it is a
    /// <see cref="CursorPage{T}"/> (F-060). The order is the display name, then the user id, so equal names page without a gap or a repeat.
    /// </summary>
    public static async Task<IResult> ListAsync(Guid id, int? limit, string? cursor, HttpContext http, PodiumDb db, ApiConfig config)
    {
        var reader = await Access.MemberAsync(db, http.User.UserId(), id, Roles.Viewer, http.RequestAborted);
        if (reader is null) return Http.NotFound();
        var seesEmail = Roles.Rank(reader.Role) >= Roles.Rank(Roles.Admin);
        var paged = limit is not null || cursor is not null;
        var take = paged ? Http.Limit(limit, config) : ListMax;
        var rows = from m in db.WorkspaceMembers.AsNoTracking()
                   join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                   where m.WorkspaceId == id && m.RemovedAt == null
                   select new { u.Id, u.DisplayName, u.Email, m.Role, m.JoinedAt };
        if (ParseCursor(cursor) is { } after)
        {
            rows = rows.Where(r => r.DisplayName.CompareTo(after.Name) > 0 || (r.DisplayName == after.Name && r.Id.CompareTo(after.Id) > 0));
        }

        var page = await rows.OrderBy(r => r.DisplayName).ThenBy(r => r.Id).Take(take + 1).ToListAsync(http.RequestAborted);
        var shown = page.Take(take).Select(r => new MemberResponse(r.Id, r.DisplayName, seesEmail ? r.Email : null, r.Role, r.JoinedAt)).ToList();
        if (!paged)
        {
            IntegrationImportEndpoints.MarkTruncated(http, page.Count > take);
            return Results.Ok(shown);
        }

        return Results.Ok(new CursorPage<MemberResponse>(shown, page.Count > take ? CursorOf(shown[^1]) : null));
    }

    private static string CursorOf(MemberResponse last) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{last.DisplayName}{CursorSeparator}{last.UserId:N}"));

    /// <summary>The name and id a cursor stands for; null for a cursor that is not one (the list then starts at its first page).</summary>
    private static (string Name, Guid Id)? ParseCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor) || !Base64Url.IsValid(cursor)) return null;
        var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
        var at = text.LastIndexOf(CursorSeparator);
        return at >= 0 && Guid.TryParseExact(text[(at + 1)..], "N", out var id) ? (text[..at], id) : null;
    }
}
