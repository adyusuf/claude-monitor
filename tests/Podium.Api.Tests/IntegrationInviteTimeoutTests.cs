using System.Net;
using Microsoft.AspNetCore.TestHost;
using Podium.Api.Data;
using Podium.Api.Mail;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>
/// The invitation mails go out inside the import (and the connection's gate): a mail server that does not answer in time stops that run's
/// invitations at the first send, so it does not hold the import for one timeout per person; the rest are tried on a later run.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationInviteTimeoutTests(ApiFactory api)
{
    /// <summary>A mail server that never answers: every send ends in the mailer's timeout.</summary>
    private sealed class HangingMailer : IMailer
    {
        private int sends;

        public int Sends => Volatile.Read(ref sends);

        public Task SendAsync(MailMessageData message, CancellationToken ct)
        {
            Interlocked.Increment(ref sends);
            throw new TimeoutException("the mail server did not answer in time");
        }
    }

    [Fact]
    public async Task A_mail_server_that_times_out_ends_the_invitations_of_the_run_at_the_first_send_and_the_import_still_succeeds()
    {
        var w = await JiraKit.ConnectedAsync(api);
        (await w.Admin.SendAsync(HttpMethod.Put, w.Base, new { autoInvite = true })).EnsureSuccessStatusCode();
        var space = w.Site.Space("TMO");
        for (var i = 0; i < 3; i++) space.People.Add(new FakePerson($"p-tmo{i}", $"Timeout {i}", Emails.New($"inv-tmo{i}")));
        space.Issue("Still imported");
        var mailer = new HangingMailer();
        await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IMailer>(mailer)));
        var admin = JiraKit.ClientOf(api, host);
        (await admin.PostAsync("/api/auth/login", new { email = w.Admin.Email, password = TestUser.Password })).EnsureSuccessStatusCode();

        var import = await admin.PostAsync($"{w.Base}/spaces/import", new { keys = new[] { "TMO" } });

        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        Assert.Equal(["Still imported"], (await JiraKit.IssuesAsync(w, await JiraKit.ProjectIdAsync(w, "TMO"))).Select(i => i.Title));
        Assert.Equal(1, mailer.Sends);
        await using var db = api.Db();
        var invitation = Assert.Single(await db.WorkspaceInvitations.AsNoTracking().Where(i => i.WorkspaceId == w.WorkspaceId).ToListAsync());
        Assert.NotNull(invitation.RevokedAt);
        Assert.All(await db.IntegrationPeople.AsNoTracking().Where(p => p.ConnectionId == w.ConnectionId).ToListAsync(), p => Assert.Null(p.InvitedAt));
    }
}
