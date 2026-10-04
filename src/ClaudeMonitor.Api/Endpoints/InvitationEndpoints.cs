using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Mail;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Text;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Endpoints;

public sealed record InviteRequest(string? Email, string? Role);
public sealed record InvitationResponse(Guid Id, string Email, string Role, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>E-mail invitations. Accepting needs the invited address on a signed-in, verified account.</summary>
public static class InvitationEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("").RequireAuthorization(Schemes.Session);
        g.MapPost("/workspaces/{id:guid}/invitations", Invite);
        g.MapGet("/workspaces/{id:guid}/invitations", List);
        g.MapDelete("/workspaces/{id:guid}/invitations/{invitationId:guid}", Revoke);
        g.MapPost("/invitations/accept", Accept);
    }

    private static async Task<IResult> Invite(Guid id, InviteRequest req, HttpContext http, MonitorDb db, ApiConfig config,
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
        var token = Secrets.NewToken();
        var invitation = new WorkspaceInvitation
        {
            WorkspaceId = id,
            EmailNormalized = SearchText.Email(req.Email!),
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
        await mailer.SendAsync(MailTemplates.Build(MailTemplates.Language(http), "invite", req.Email!.Trim(),
            Http.Link(config, "/invitations/accept", token), inviter.DisplayName, workspace.Name), http.RequestAborted);
        return Results.Created($"/api/workspaces/{id}/invitations/{invitation.Id}", new { invitation.Id });
    }

    private static async Task<IResult> List(Guid id, HttpContext http, MonitorDb db, TimeProvider clock)
    {
        if (await Access.MemberAsync(db, http.User.UserId(), id, Roles.Admin, http.RequestAborted) is null) return Http.NotFound();
        var now = clock.GetUtcNow();
        var open = await db.WorkspaceInvitations.AsNoTracking()
            .Where(i => i.WorkspaceId == id && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new InvitationResponse(i.Id, i.EmailNormalized, i.Role, i.CreatedAt, i.ExpiresAt))
            .ToListAsync(http.RequestAborted);
        return Results.Ok(open);
    }

    private static async Task<IResult> Revoke(Guid id, Guid invitationId, HttpContext http, MonitorDb db, TimeProvider clock)
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

    private static async Task<IResult> Accept(TokenRequest req, HttpContext http, MonitorDb db, TimeProvider clock)
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

        var member = await db.WorkspaceMembers.FirstOrDefaultAsync(
            m => m.WorkspaceId == invitation.WorkspaceId && m.UserId == userId, http.RequestAborted);
        if (member is null)
        {
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = invitation.WorkspaceId, UserId = userId, Role = invitation.Role, JoinedAt = now });
        }
        else if (member.RemovedAt is not null || Roles.Rank(invitation.Role) > Roles.Rank(member.Role))
        {
            member.RemovedAt = null;
            member.Role = invitation.Role;
            member.JoinedAt = now;
        }

        invitation.AcceptedAt = now;
        Audit.Add(db, http, clock, AuditActions.InvitationAccepted, invitation.WorkspaceId, userId,
            targetType: "invitation", targetId: invitation.Id);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { workspaceId = invitation.WorkspaceId });
    }
}
