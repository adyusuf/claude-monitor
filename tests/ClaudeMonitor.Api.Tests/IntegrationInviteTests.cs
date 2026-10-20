using System.Net;
using Microsoft.AspNetCore.TestHost;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Mail;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Automatic invitation of the people of a Jira space (ADR-0015 §1 decision 4): who is mailed, how often, and what a mail server's failure does.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationInviteTests(ApiFactory api)
{
    private int MailsTo(string address) => api.Mail.Sent.Count(m => string.Equals(m.To, address, StringComparison.OrdinalIgnoreCase));

    private static Task<HttpResponseMessage> SwitchAsync(JiraWorld w, bool on) => w.Admin.SendAsync(HttpMethod.Put, w.Base, new { autoInvite = on });

    private async Task<List<WorkspaceInvitation>> InvitationsAsync(JiraWorld w)
    {
        await using var db = api.Db();
        return await db.WorkspaceInvitations.AsNoTracking().Where(i => i.WorkspaceId == w.WorkspaceId).ToListAsync();
    }

    [Fact]
    public async Task Nobody_is_mailed_while_the_switch_is_off()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var address = Emails.New("inv-off");
        w.Site.Space("OFF").People.Add(new FakePerson("p-off", "Off Person", address));

        await JiraKit.ImportAsync(w, "OFF");
        await JiraKit.PassAsync(w);

        Assert.Equal(0, MailsTo(address));
        Assert.Empty(await InvitationsAsync(w));
    }

    [Fact]
    public async Task With_the_switch_on_a_person_with_a_visible_e_mail_gets_one_viewer_invitation_and_never_a_second()
    {
        var w = await JiraKit.ConnectedAsync(api);
        (await SwitchAsync(w, true)).EnsureSuccessStatusCode();
        var invitee = await api.NewClient().SignedUpAsync("inv-on"); // has an account of their own, but is no member here
        w.Site.Space("ON").People.Add(new FakePerson("p-on", "On Person", invitee.Email.ToUpperInvariant()));

        await JiraKit.ImportAsync(w, "ON");
        await JiraKit.PassAsync(w);
        await JiraKit.ImportAsync(w, "ON");

        var sent = api.Mail.Sent.Where(m => string.Equals(m.To, invitee.Email, StringComparison.OrdinalIgnoreCase) && m.Body.Contains("/invitations/accept", StringComparison.Ordinal)).ToList();
        Assert.Single(sent);
        var invitation = Assert.Single(await InvitationsAsync(w));
        Assert.Equal((invitee.Email.ToLowerInvariant(), Roles.Viewer, w.Admin.Id, null, null), (invitation.EmailNormalized, invitation.Role, invitation.InvitedBy, invitation.AcceptedAt, invitation.RevokedAt));
        Assert.True(invitation.ExpiresAt > api.Clock.GetUtcNow());
        await using var db = api.Db();
        Assert.NotNull((await db.IntegrationPeople.AsNoTracking().SingleAsync(p => p.ConnectionId == w.ConnectionId && p.ExternalAccountId == "p-on")).InvitedAt);
        Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.WorkspaceId == w.WorkspaceId && a.Action == "integration.person_invited"));

        // The mailed link works: accepting makes the person a viewer, and the next read links them to their Jira person.
        (await invitee.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(invitee.Email) })).EnsureSuccessStatusCode();
        Assert.Equal(Roles.Viewer, (await db.WorkspaceMembers.AsNoTracking().SingleAsync(m => m.WorkspaceId == w.WorkspaceId && m.UserId == invitee.Id)).Role);
        await JiraKit.PassAsync(w);
        Assert.Equal(invitee.Id, (await db.IntegrationPeople.AsNoTracking().SingleAsync(p => p.ConnectionId == w.ConnectionId && p.ExternalAccountId == "p-on")).UserId);
    }

    [Fact]
    public async Task No_mail_goes_to_somebody_without_an_address_an_inactive_or_app_account_a_member_or_a_person_already_invited()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var member = await RemoteKit.MemberAsync(api, w.Admin, "inv-member");
        var pending = Emails.New("inv-pending");
        (await w.Admin.PostAsync($"/api/workspaces/{w.WorkspaceId}/invitations", new { email = pending, role = "member" })).EnsureSuccessStatusCode();
        var memberMails = MailsTo(member.Email);
        var outsider = Emails.New("inv-outsider");
        var space = w.Site.Space("NOT");
        space.People.AddRange(
        [
            new FakePerson("p-none", "No Address"),
            new FakePerson("p-inactive", "Inactive", Emails.New("inv-inactive")) { Active = false },
            new FakePerson("p-app", "Robot", Emails.New("inv-app")) { AccountType = "app" },
            new FakePerson("p-member", "A Member", member.Email),
            new FakePerson("p-pending", "Pending", pending),
            new FakePerson("p-outsider", "Outsider", outsider),
        ]);
        (await SwitchAsync(w, true)).EnsureSuccessStatusCode();

        await JiraKit.ImportAsync(w, "NOT");

        var mails = api.Mail.Sent.ToList();
        Assert.Equal(1, MailsTo(outsider));
        Assert.Equal(1, MailsTo(pending)); // only the invitation the admin sent
        Assert.Equal(memberMails, MailsTo(member.Email));
        Assert.DoesNotContain(mails, m => m.To.Contains("inv-inactive", StringComparison.Ordinal) || m.To.Contains("inv-app", StringComparison.Ordinal));
        await using var db = api.Db();
        var people = await db.IntegrationPeople.AsNoTracking().Where(p => p.ConnectionId == w.ConnectionId).ToDictionaryAsync(p => p.ExternalAccountId);
        Assert.Equal(member.Id, people["p-member"].UserId);
        Assert.Equal(["p-outsider"], people.Values.Where(p => p.InvitedAt != null).Select(p => p.ExternalAccountId));
    }

    [Fact]
    public async Task At_most_fifty_are_invited_in_one_run_and_the_rest_in_the_next()
    {
        var w = await JiraKit.ConnectedAsync(api);
        (await SwitchAsync(w, true)).EnsureSuccessStatusCode();
        var space = w.Site.Space("CAP");
        var addresses = Enumerable.Range(0, 60).Select(i => Emails.New($"inv-cap{i}")).ToList();
        for (var i = 0; i < 60; i++) space.People.Add(new FakePerson($"p-{i}", $"Person {i}", addresses[i]));

        await JiraKit.ImportAsync(w, "CAP");
        var first = (await InvitationsAsync(w)).Count;
        await JiraKit.PassAsync(w);
        var second = await InvitationsAsync(w);
        await JiraKit.PassAsync(w);

        Assert.Equal(50, first);
        Assert.Equal(60, second.Count);
        Assert.Equal(60, (await InvitationsAsync(w)).Count);
        Assert.Equal(60, second.Select(i => i.EmailNormalized).Distinct().Count());
        Assert.All(addresses, a => Assert.Equal(1, MailsTo(a)));
    }

    [Fact]
    public async Task A_mail_server_that_is_down_leaves_the_person_uninvited_and_the_import_intact_until_it_is_up()
    {
        var w = await JiraKit.ConnectedAsync(api);
        (await SwitchAsync(w, true)).EnsureSuccessStatusCode();
        var address = Emails.New("inv-flaky");
        var space = w.Site.Space("FLK");
        space.People.Add(new FakePerson("p-flaky", "Flaky", address));
        space.Issue("Still imported");
        var mailer = new FlakyMailer(api.Mail) { Down = true };
        await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IMailer>(mailer)));
        var admin = JiraKit.ClientOf(api, host);
        (await admin.PostAsync("/api/auth/login", new { email = w.Admin.Email, password = TestUser.Password })).EnsureSuccessStatusCode();

        var import = await admin.PostAsync($"{w.Base}/spaces/import", new { keys = new[] { "FLK" } });

        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        Assert.Equal(["Still imported"], (await JiraKit.IssuesAsync(w, await JiraKit.ProjectIdAsync(w, "FLK"))).Select(i => i.Title));
        Assert.Equal(0, MailsTo(address));
        var failed = Assert.Single(await InvitationsAsync(w));
        Assert.NotNull(failed.RevokedAt); // the row stays for the admin to see, but cannot be used
        await using (var db = api.Db())
        {
            Assert.Null((await db.IntegrationPeople.AsNoTracking().SingleAsync(p => p.ConnectionId == w.ConnectionId && p.ExternalAccountId == "p-flaky")).InvitedAt);
        }

        mailer.Down = false;
        (await admin.PostAsync($"{w.Base}/sync")).EnsureSuccessStatusCode();

        Assert.Equal(1, MailsTo(address));
        Assert.Equal(2, (await InvitationsAsync(w)).Count);
        Assert.Single(await InvitationsAsync(w), i => i.RevokedAt is null);
    }
}
