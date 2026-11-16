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

    /// <summary>Inviting and creating a workspace have their own per-IP budget (<see cref="ApiConfig.InviteRequestsPerMinute"/>), apart from the sign-in one.</summary>
    public const string RateLimitPolicy = "invite";

    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapPost("/workspaces/{id:guid}/invitations", Invite).RequireRateLimiting(RateLimitPolicy);
        g.MapGet("/workspaces/{id:guid}/invitations", List);
        g.MapDelete("/workspaces/{id:guid}/invitations/{invitationId:guid}", Revoke);
        g.MapPost("/invitations/accept", Accept);
    }

    private static async Task<IResult> Invite(Guid id, InviteRequest req, HttpContext http, PodiumDb db, ApiConfig config,
        TimeProvider clock, IMailer mailer)
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
        var memberRole = await db.WorkspaceMembers.AsNoTracking().Where(m => m.WorkspaceId == id && m.RemovedAt == null)
            .Join(db.Users.Where(u => u.EmailNormalized == normalized), m => m.UserId, u => u.Id, (m, _) => m.Role)
            .FirstOrDefaultAsync(http.RequestAborted);
        if (memberRole is not null && Roles.Rank(memberRole) >= Roles.Rank(role))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: AlreadyMember);
        }

        if (await db.WorkspaceInvitations.AnyAsync(i => i.WorkspaceId == id && i.EmailNormalized == normalized && i.AcceptedAt == null
                && i.RevokedAt == null && i.ExpiresAt > now, http.RequestAborted))
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

        var inviter = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, http.RequestAborted);
        var workspace = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == id, http.RequestAborted);
        var inviteeLanguage = await db.Users.AsNoTracking().Where(u => u.EmailNormalized == invitation.EmailNormalized).Select(u => u.Language).FirstOrDefaultAsync(http.RequestAborted);
        await mailer.SendAsync(MailTemplates.Build(MailTemplates.Language(http, inviteeLanguage), "invite", req.Email!.Trim(),
            Http.Link(config, "/invitations/accept", token), inviter.DisplayName, workspace.Name), http.RequestAborted);
        return Results.Created($"/api/workspaces/{id}/invitations/{invitation.Id}", new { invitation.Id });
    }

    private static async Task<IResult> List(Guid id, HttpContext http, PodiumDb db, TimeProvider clock)
    {
        if (await Access.MemberAsync(db, http.User.UserId(), id, Roles.Admin, http.RequestAborted) is null) return Http.NotFound();
        var now = clock.GetUtcNow();
        var open = await db.WorkspaceInvitations.AsNoTracking()
            .Where(i => i.WorkspaceId == id && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InvitationResponse(i.Id, i.Email ?? i.EmailNormalized, i.Role, i.CreatedAt, i.ExpiresAt))
            .ToListAsync(http.RequestAborted);
        return Results.Ok(open);
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
        if (user.EmailVerifiedAt is null || user.EmailNormalized != invitation.EmailNormalized)
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
