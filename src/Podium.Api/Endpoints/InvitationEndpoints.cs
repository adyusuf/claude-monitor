using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Mail;
using Podium.Api.Security;
using Podium.Api.Text;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

public sealed record InviteRequest(string? Email, string? Role);
public sealed record InvitationResponse(Guid Id, string Email, string Role, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>E-mail invitations. Accepting needs the invited address on a signed-in, verified account.</summary>
public static class InvitationEndpoints
{
    /// <summary>409 title: the address is an active member already at the invited rank or above (the invitation could change nothing).</summary>
    public const string AlreadyMember = "already_member";
    /// <summary>409 title: the address has an open invitation to this workspace (revoke it to send a new one).</summary>
    public const string InvitationPending = "invitation_pending";
    /// <summary>409 title: an invitation from before the typed address was kept cannot be sent again; a fresh one is needed.</summary>
    public const string InvitationAddressUnknown = "invitation_address_unknown";

    /// <summary>Inviting and creating a workspace have their own per-IP budget (<see cref="ApiConfig.InviteRequestsPerMinute"/>), apart from the sign-in one.</summary>
    public const string RateLimitPolicy = "invite";

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapPost("/workspaces/{id:guid}/invitations", Invite).RequireRateLimiting(RateLimitPolicy);
        g.MapGet("/workspaces/{id:guid}/invitations", List);
        g.MapPost("/workspaces/{id:guid}/invitations/{invitationId:guid}/resend", Resend).RequireRateLimiting(RateLimitPolicy);
        g.MapDelete("/workspaces/{id:guid}/invitations/{invitationId:guid}", Revoke);
        g.MapPost("/invitations/accept", Accept);
    }

    private static async Task<IResult> Invite(Guid id, InviteRequest req, HttpContext http, PodiumDb db, ApiConfig config,
        TimeProvider clock, IMailer mailer, ILoggerFactory logs)
    {
        var userId = http.User.UserId();
        var actor = await Access.MemberAsync(db, userId, id, Roles.Admin, http.RequestAborted);
        if (actor is null) return Http.NotFound();
        if (!Http.IsEmail(req.Email)) return Http.Invalid("email", "invalid_email");
        var role = req.Role ?? Roles.Member;
        if (Roles.Rank(role) == 0) return Http.Invalid("role", "invalid_role");
        if (Roles.Rank(role) > Roles.Rank(actor.Role)) return Results.Forbid();

        var now = clock.GetUtcNow();
        var normalized = SearchText.Email(req.Email!);
        var key = EmailIdentity.Key(req.Email);
        var memberRole = await db.WorkspaceMembers.AsNoTracking().Where(m => m.WorkspaceId == id && m.RemovedAt == null)
            .Join(db.Users.Where(u => u.EmailKey == key), m => m.UserId, u => u.Id, (m, _) => m.Role)
            .FirstOrDefaultAsync(http.RequestAborted);
        if (memberRole is not null && Roles.Rank(memberRole) >= Roles.Rank(role))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: AlreadyMember);
        }

        // Folding finds every candidate; the identity key decides (F-180). An invitation from before the typed address was kept counts as the same.
        var pending = await db.WorkspaceInvitations.AsNoTracking().Where(i => i.WorkspaceId == id && i.EmailNormalized == normalized
                && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .Select(i => i.Email).ToListAsync(http.RequestAborted);
        if (pending.Any(e => e is null || EmailIdentity.Key(e) == key))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: InvitationPending);
        }

        var token = Secrets.NewToken();
        var invitation = new WorkspaceInvitation
        {
            WorkspaceId = id,
            EmailNormalized = normalized,
            Email = req.Email!.Trim(),
            Role = role,
            TokenHash = Secrets.Hash(token),
            InvitedBy = userId,
            CreatedAt = now,
            ExpiresAt = now + config.InvitationLifetime,
        };
        db.WorkspaceInvitations.Add(invitation);
        Audit.Add(db, http, clock, AuditActions.InvitationCreated, id, userId, targetType: "invitation", targetId: invitation.Id,
            detail: new { role });
        await db.SaveChangesAsync(http.RequestAborted);

        // Saved above: a mail server that is down leaves the invitation pending and says so, and an admin can send it again.
        var mailSent = await InvitationMail.TrySendAsync(http, db, config, mailer, logs, invitation, token, userId);
        return Results.Created($"/api/workspaces/{id}/invitations/{invitation.Id}", new { invitation.Id, mailSent });
    }

    /// <summary>Sends a pending invitation again with a new link (the old one stops working); says whether the mail went.</summary>
    private static async Task<IResult> Resend(Guid id, Guid invitationId, HttpContext http, PodiumDb db, ApiConfig config,
        TimeProvider clock, IMailer mailer, ILoggerFactory logs)
    {
        var userId = http.User.UserId();
        if (await Access.MemberAsync(db, userId, id, Roles.Admin, http.RequestAborted) is null) return Http.NotFound();
        var now = clock.GetUtcNow();
        // A row from before the typed address was kept has only the folded one, which may be another mailbox than the person's (F-180): no mail goes there.
        var open = await db.WorkspaceInvitations.AsNoTracking().Where(i => i.Id == invitationId && i.WorkspaceId == id && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .Select(i => new { i.Email }).FirstOrDefaultAsync(http.RequestAborted);
        if (open is null) return Http.NotFound();
        if (open.Email is null) return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: InvitationAddressUnknown);
        var token = Secrets.NewToken();
        var hash = Secrets.Hash(token);
        // Compare-and-set on the still-open invitation: one that was accepted, revoked or has expired since cannot be given a new link.
        var renewed = await db.WorkspaceInvitations
            .Where(i => i.Id == invitationId && i.WorkspaceId == id && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.TokenHash, hash), http.RequestAborted);
        if (renewed != 1) return Http.NotFound();

        var invitation = await db.WorkspaceInvitations.AsNoTracking().FirstAsync(i => i.Id == invitationId, http.RequestAborted);
        Audit.Add(db, http, clock, AuditActions.InvitationResent, id, userId, targetType: "invitation", targetId: invitationId);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { mailSent = await InvitationMail.TrySendAsync(http, db, config, mailer, logs, invitation, token, userId) });
    }

    /// <summary>
    /// The open invitations, newest first (the id breaks equal times). With neither <paramref name="limit"/> nor <paramref name="cursor"/>
    /// the answer is the plain array it always was, capped at <see cref="WorkspaceMemberList.ListMax"/>; with either it is a
    /// <see cref="CursorPage{T}"/> (F-060).
    /// </summary>
    private static async Task<IResult> List(Guid id, int? limit, string? cursor, HttpContext http, PodiumDb db, TimeProvider clock, ApiConfig config)
    {
        if (await Access.MemberAsync(db, http.User.UserId(), id, Roles.Admin, http.RequestAborted) is null) return Http.NotFound();
        var now = clock.GetUtcNow();
        var paged = limit is not null || cursor is not null;
        var take = paged ? Http.Limit(limit, config) : WorkspaceMemberList.ListMax;
        var query = db.WorkspaceInvitations.AsNoTracking()
            .Where(i => i.WorkspaceId == id && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now);
        if (Cursor.Parse(cursor) is { } after)
        {
            query = query.Where(i => i.CreatedAt < after.At || (i.CreatedAt == after.At && i.Id.CompareTo(after.Id) < 0));
        }

        var page = await query.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id).Take(take + 1).ToListAsync(http.RequestAborted);
        var shown = page.Take(take).ToList();
        var rows = shown.Select(i => new InvitationResponse(i.Id, i.Email ?? i.EmailNormalized, i.Role, i.CreatedAt, i.ExpiresAt)).ToList();
        if (!paged) return Results.Ok(rows);
        return Results.Ok(new CursorPage<InvitationResponse>(rows, page.Count > take ? Cursor.Of(shown[^1].CreatedAt, shown[^1].Id) : null));
    }

    private static async Task<IResult> Revoke(Guid id, Guid invitationId, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        var userId = http.User.UserId();
        if (await Access.MemberAsync(db, userId, id, Roles.Admin, http.RequestAborted) is null) return Http.NotFound();
        var invitation = await db.WorkspaceInvitations.FirstOrDefaultAsync(
            i => i.Id == invitationId && i.WorkspaceId == id && i.AcceptedAt == null && i.RevokedAt == null, http.RequestAborted);
        if (invitation is null) return Http.NotFound();
        invitation.RevokedAt = clock.GetUtcNow();
        Audit.Add(db, http, clock, AuditActions.InvitationRevoked, id, userId, targetType: "invitation", targetId: invitationId);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    /// <summary>
    /// Whether the invitation was sent to the user's address: by the identity key (F-180), so a look-alike address is another person; an
    /// invitation from before the typed address was kept has only the folded form to compare.
    /// </summary>
    private static bool Addressed(User user, WorkspaceInvitation invitation) =>
        invitation.Email is null ? user.EmailNormalized == invitation.EmailNormalized : user.EmailKey == EmailIdentity.Key(invitation.Email);

    private static async Task<IResult> Accept(TokenRequest req, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        if (string.IsNullOrEmpty(req.Token)) return Http.Invalid("token", "invalid_token");
        var now = clock.GetUtcNow();
        var hash = Secrets.Hash(req.Token);
        var invitation = await db.WorkspaceInvitations.FirstOrDefaultAsync(
            i => i.TokenHash == hash && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now, http.RequestAborted);
        if (invitation is null) return Http.Invalid("token", "invalid_token");

        var userId = http.User.UserId();
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, http.RequestAborted);
        if (user.EmailVerifiedAt is null || !Addressed(user, invitation))
        {
            return Http.Invalid("token", "invitation_other_address");
        }

        // The invitation speaks for its inviter: one who has left, or could no longer give this role (an admin or owner of at least
        // its rank), voids it.
        var inviter = await db.WorkspaceMembers.AsNoTracking().FirstOrDefaultAsync(
            m => m.WorkspaceId == invitation.WorkspaceId && m.UserId == invitation.InvitedBy && m.RemovedAt == null, http.RequestAborted);
        if (inviter is null || Roles.Rank(inviter.Role) < Math.Max(Roles.Rank(Roles.Admin), Roles.Rank(invitation.Role)))
        {
            return Http.Invalid("token", "invalid_token");
        }

        // An archived workspace takes no new member (/me hides it): refused, and the invitation stays unspent.
        if (!await db.Workspaces.AsNoTracking().AnyAsync(w => w.Id == invitation.WorkspaceId && w.Status == WorkspaceStatuses.Active, http.RequestAborted))
        {
            return Http.Invalid("token", "invalid_token");
        }

        var member = await db.WorkspaceMembers.FirstOrDefaultAsync(
            m => m.WorkspaceId == invitation.WorkspaceId && m.UserId == userId, http.RequestAborted);

        // Claimed with a compare-and-set in the same transaction as the membership: a revoke or a second accept that
        // committed after the read above leaves 0 rows, and this one changes nothing.
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);
        var claimed = await db.WorkspaceInvitations
            .Where(i => i.Id == invitation.Id && i.AcceptedAt == null && i.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.AcceptedAt, now), http.RequestAborted);
        if (claimed != 1) return Http.Invalid("token", "invalid_token");

        if (member is null)
        {
            // Two invitations of this person to the workspace accepted at once both read no row: the insert that meets the other's key
            // does nothing (a plain Add would end the request, and its transaction, in a 500), and the row it lost to is the membership.
            var added = await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO workspace_members (workspace_id, user_id, role, joined_at) VALUES ({invitation.WorkspaceId}, {userId}, {invitation.Role}, {now}) ON CONFLICT DO NOTHING",
                http.RequestAborted);
            if (added == 0)
            {
                member = await db.WorkspaceMembers.FirstAsync(m => m.WorkspaceId == invitation.WorkspaceId && m.UserId == userId, http.RequestAborted);
            }
        }

        if (member is not null && (member.RemovedAt is not null || Roles.Rank(invitation.Role) > Roles.Rank(member.Role)))
        {
            // A member who comes back starts again; an active one moved up keeps the date they first joined.
            if (member.RemovedAt is not null) member.JoinedAt = now;
            member.RemovedAt = null;
            member.Role = invitation.Role;
        }

        Audit.Add(db, http, clock, AuditActions.InvitationAccepted, invitation.WorkspaceId, userId,
            targetType: "invitation", targetId: invitation.Id);
        await db.SaveChangesAsync(http.RequestAborted);
        await tx.CommitAsync(http.RequestAborted);
        return Results.Ok(new { workspaceId = invitation.WorkspaceId });
    }
}
