using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Mail;
using Podium.Api.Security;
using Podium.Api.Text;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Integrations;

/// <summary>
/// The tool's people (ADR-0015 §1 decision 4): one row per person per connection, matched to a workspace member by a visible e-mail address, and
/// invited as a viewer when the connection says so. Nobody is invited without an address, twice, or past <see cref="InviteCap"/> in one run.
/// </summary>
public sealed class PeopleMirror(PodiumDb db, ApiConfig config, IMailer mailer, TimeProvider clock, ILogger<PeopleMirror> log)
{
    public const int InviteCap = 50;

    public async Task<Dictionary<string, IntegrationPerson>> UpsertAsync(IntegrationConnection connection, IEnumerable<ExternalPerson> people,
        MirrorCounts counts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var wanted = people.GroupBy(p => p.Id).Select(g => g.First()).ToList();
        var ids = wanted.Select(p => p.Id).ToList();
        var known = await db.IntegrationPeople.Where(p => p.ConnectionId == connection.Id && ids.Contains(p.ExternalAccountId))
            .ToDictionaryAsync(p => p.ExternalAccountId, ct);
        var now = clock.GetUtcNow();
        var emails = wanted.Where(p => p.Email is not null).Select(p => SearchText.Email(p.Email!)).Distinct().ToList();
        var members = await (from u in db.Users.AsNoTracking()
                             join m in db.WorkspaceMembers.AsNoTracking() on u.Id equals m.UserId
                             where m.WorkspaceId == connection.WorkspaceId && m.RemovedAt == null && u.EmailNormalized != null && emails.Contains(u.EmailNormalized)
                             select new { u.Id, Email = u.EmailNormalized! }).ToDictionaryAsync(x => x.Email, x => x.Id, ct);
        foreach (var p in wanted)
        {
            if (!known.TryGetValue(p.Id, out var row))
            {
                row = new IntegrationPerson { ConnectionId = connection.Id, ExternalAccountId = p.Id, FirstSeenAt = now };
                db.IntegrationPeople.Add(row);
                known[p.Id] = row;
                counts.People++;
            }

            row.DisplayName = p.DisplayName;
            row.Active = p.Active;
            row.LastSeenAt = now;
            if (p.Email is not null)
            {
                row.Email = p.Email;
                row.EmailNormalized = SearchText.Email(p.Email);
                if (row.UserId is null && members.TryGetValue(row.EmailNormalized, out var user)) row.UserId = user;
            }
        }

        await db.SaveChangesAsync(ct);
        return known;
    }

    /// <summary>Invites the people of this connection who have a visible address, are not members, and were not invited. Returns how many mails went out.</summary>
    public async Task<int> InviteAsync(IntegrationConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!connection.AutoInvite) return 0;
        var now = clock.GetUtcNow();
        var candidates = await db.IntegrationPeople.Where(p => p.ConnectionId == connection.Id && p.Active && p.UserId == null && p.InvitedAt == null && p.EmailNormalized != null)
            .OrderBy(p => p.FirstSeenAt).Take(InviteCap).ToListAsync(ct);
        if (candidates.Count == 0) return 0;
        var inviter = await db.Users.AsNoTracking().FirstAsync(u => u.Id == connection.ConnectorUserId, ct);
        var workspace = await db.Workspaces.AsNoTracking().FirstAsync(w => w.Id == connection.WorkspaceId, ct);
        var sent = 0;
        foreach (var person in candidates)
        {
            var normalized = person.EmailNormalized!;
            if (await HasOpenInvitationAsync(connection.WorkspaceId, normalized, now, ct)) continue;
            var token = Secrets.NewToken();
            var invitation = new WorkspaceInvitation
            {
                WorkspaceId = connection.WorkspaceId,
                EmailNormalized = normalized,
                Role = Roles.Viewer,
                TokenHash = Secrets.Hash(token),
                InvitedBy = connection.ConnectorUserId,
                CreatedAt = now,
                ExpiresAt = now + config.InvitationLifetime,
            };
            db.WorkspaceInvitations.Add(invitation);
            Audit.Add(db, null, clock, AuditActions.IntegrationInvited, connection.WorkspaceId, connection.ConnectorUserId, targetType: "invitation", targetId: invitation.Id);
            await db.SaveChangesAsync(ct);
            var language = await db.Users.AsNoTracking().Where(u => u.EmailNormalized == normalized).Select(u => u.Language).FirstOrDefaultAsync(ct);
            try
            {
                await mailer.SendAsync(MailTemplates.Build(language is not null && MailTemplates.Languages.Contains(language) ? language : "en", "invite", person.Email!.Trim(),
                    Http.Link(config, "/invitations/accept", token), inviter.DisplayName, workspace.Name), ct);
                person.InvitedAt = now;
                sent++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The invitation row stays (an admin sees it and can revoke it); the person is tried again on a later run.
                invitation.RevokedAt = now;
                log.LogWarning("Invitation mail for integration {Connection} failed: {Type}", connection.Id, e.GetType().Name);
            }

            await db.SaveChangesAsync(ct);
        }

        return sent;
    }

    private Task<bool> HasOpenInvitationAsync(Guid workspaceId, string normalized, DateTimeOffset now, CancellationToken ct) =>
        db.WorkspaceInvitations.AnyAsync(i => i.WorkspaceId == workspaceId && i.EmailNormalized == normalized && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now, ct);
}
