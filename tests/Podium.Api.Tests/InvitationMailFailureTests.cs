using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Podium.Api.Mail;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// F-047: the invitation is saved before its mail goes out. A mail server that is down must not turn the invite into a 500: the invitation
/// stays, the answer says <c>mailSent: false</c>, and an admin sends the mail again with the resend endpoint.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class InvitationMailFailureTests(ApiFactory api)
{
    private static string Base(TestUser owner) => $"/api/workspaces/{owner.WorkspaceId}/invitations";

    private async Task<(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host, TestUser Admin, FlakyMailer Mailer)> HostAsync(TestUser owner)
    {
        var mailer = new FlakyMailer(api.Mail) { Down = true };
        var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IMailer>(mailer)));
        return (host, await HostKit.SignInAsync(api, host, owner), mailer);
    }

    [Fact]
    public async Task With_the_mail_server_down_the_invite_keeps_the_invitation_and_answers_mail_not_sent_instead_of_500()
    {
        var owner = await api.NewClient().SignedUpAsync("mailf-owner");
        var address = Emails.New("mailf-invitee");
        var (host, admin, _) = await HostAsync(owner);
        await using (host)
        {
            var response = await admin.PostAsync(Base(owner), new { email = address, role = "member" });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(body.GetProperty("mailSent").GetBoolean());
            var id = body.GetProperty("id").GetGuid();
            Assert.Equal(id, (await owner.GetJsonAsync(Base(owner)))[0].GetProperty("id").GetGuid());
            Assert.DoesNotContain(api.Mail.Sent, m => m.To == address);
            await using var db = api.Db();
            Assert.True(await db.AuditEvents.AnyAsync(a => a.WorkspaceId == owner.WorkspaceId && a.Action == AuditActions.InvitationCreated && a.TargetId == id.ToString()));
        }
    }

    [Fact]
    public async Task A_resend_after_the_failure_sends_the_mail_with_a_link_that_accepts_and_is_audited()
    {
        var owner = await api.NewClient().SignedUpAsync("mailr-owner");
        var invitee = await api.NewClient().SignedUpAsync("mailr-invitee");
        var (host, admin, mailer) = await HostAsync(owner);
        await using (host)
        {
            var id = (await (await admin.PostAsync(Base(owner), new { email = invitee.Email, role = "member" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

            var stillDown = await admin.PostAsync($"{Base(owner)}/{id}/resend");
            Assert.Equal(HttpStatusCode.OK, stillDown.StatusCode);
            Assert.False((await stillDown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mailSent").GetBoolean());

            mailer.Down = false;
            var resent = await admin.PostAsync($"{Base(owner)}/{id}/resend");

            Assert.Equal(HttpStatusCode.OK, resent.StatusCode);
            Assert.True((await resent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mailSent").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, (await invitee.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(invitee.Email) })).StatusCode);
            await using var db = api.Db();
            // One row for each resend, the failed one too: the admin did resend.
            var rows = await db.AuditEvents.AsNoTracking().Where(a => a.WorkspaceId == owner.WorkspaceId && a.Action == AuditActions.InvitationResent && a.TargetId == id.ToString()).ToListAsync();
            Assert.Equal([owner.Id, owner.Id], rows.Select(r => r.ActorUserId));
        }
    }

    [Fact]
    public async Task A_normal_invite_says_mail_sent_and_a_resend_replaces_the_link_so_the_old_one_stops_working()
    {
        var owner = await api.NewClient().SignedUpAsync("mailo-owner");
        var invitee = await api.NewClient().SignedUpAsync("mailo-invitee");
        var invited = await owner.PostAsync(Base(owner), new { email = invitee.Email, role = "member" });
        var invitedBody = await invited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(invitedBody.GetProperty("mailSent").GetBoolean());
        var id = invitedBody.GetProperty("id").GetGuid();
        var oldToken = api.Mail.TokenFor(invitee.Email);

        (await owner.PostAsync($"{Base(owner)}/{id}/resend")).EnsureSuccessStatusCode();

        var newToken = api.Mail.TokenFor(invitee.Email);
        Assert.NotEqual(oldToken, newToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await invitee.PostAsync("/api/invitations/accept", new { token = oldToken })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await invitee.PostAsync("/api/invitations/accept", new { token = newToken })).StatusCode);
    }

    [Fact]
    public async Task Only_a_pending_invitation_of_the_workspace_is_resent_and_only_by_an_admin()
    {
        var owner = await api.NewClient().SignedUpAsync("mailp-owner");
        var other = await api.NewClient().SignedUpAsync("mailp-other");
        var member = await RemoteKit.MemberAsync(api, owner, "mailp-member");
        var revoked = (await (await owner.PostAsync(Base(owner), new { email = Emails.New("mailp-a"), role = "member" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await owner.SendAsync(HttpMethod.Delete, $"{Base(owner)}/{revoked}")).EnsureSuccessStatusCode();
        var pending = (await (await owner.PostAsync(Base(owner), new { email = Emails.New("mailp-b"), role = "member" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var sentBefore = api.Mail.Sent.Count;

        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"{Base(owner)}/{revoked}/resend")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"{Base(owner)}/{Guid.NewGuid()}/resend")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.PostAsync($"{Base(owner)}/{pending}/resend")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"{Base(owner)}/{pending}/resend")).StatusCode);
        Assert.Equal(sentBefore, api.Mail.Sent.Count);
    }
}
