using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Mail;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Endpoints;

/// <summary>The invitation mail: sent after the invitation is saved, and sent again by the resend endpoint (F-047).</summary>
public static class InvitationMail
{
    /// <summary>
    /// Sends the invitation link. False when the mail server refused or was unreachable: the invitation stays and the caller says "mail not
    /// sent" (an admin sends it again); the failure is logged by its type only, never the address or the link.
    /// </summary>
    public static async Task<bool> TrySendAsync(HttpContext http, PodiumDb db, ApiConfig config, IMailer mailer, ILoggerFactory logs,
        WorkspaceInvitation invitation, string token, Guid inviterId)
    {
        var inviter = await db.Users.AsNoTracking().FirstAsync(u => u.Id == inviterId, http.RequestAborted);
        var workspace = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == invitation.WorkspaceId, http.RequestAborted);
        var inviteeLanguage = await db.Users.AsNoTracking().Where(u => u.EmailNormalized == invitation.EmailNormalized).Select(u => u.Language)
            .FirstOrDefaultAsync(http.RequestAborted);
        try
        {
            await mailer.SendAsync(MailTemplates.Build(MailTemplates.Language(http, inviteeLanguage), "invite", invitation.Email ?? invitation.EmailNormalized,
                Http.Link(config, "/invitations/accept", token), inviter.DisplayName, workspace.Name), http.RequestAborted);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logs.CreateLogger(typeof(InvitationMail)).LogWarning("Invitation mail failed: {Type}", e.GetType().Name);
            return false;
        }
    }
}
