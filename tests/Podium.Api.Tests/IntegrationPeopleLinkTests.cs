using System.Net;
using Podium.Api.Data;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Podium.Api.Tests;

/// <summary>The link between a person of the tool and a member (F-068): it can be undone, and a member stands for one person per connection.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationPeopleLinkTests(ApiFactory api)
{
    private async Task<(JiraWorld World, Guid Hidden, Guid Other)> WorldAsync(Action<FakeSpace>? seed = null)
    {
        var w = await JiraKit.ConnectedAsync(api);
        var space = w.Site.Space("LNK");
        space.People.AddRange([new FakePerson("p-hidden", "Hidden Person"), new FakePerson("p-other", "Other Person")]);
        seed?.Invoke(space);
        await JiraKit.ImportAsync(w, "LNK");
        await using var db = api.Db();
        var people = await db.IntegrationPeople.AsNoTracking().Where(p => p.ConnectionId == w.ConnectionId).ToDictionaryAsync(p => p.ExternalAccountId, p => p.Id);
        return (w, people["p-hidden"], people["p-other"]);
    }

    private async Task<Guid?> UserOfAsync(Guid person)
    {
        await using var db = api.Db();
        return await db.IntegrationPeople.AsNoTracking().Where(p => p.Id == person).Select(p => p.UserId).SingleAsync();
    }

    private static Task<HttpResponseMessage> PutAsync(JiraWorld w, Guid person, object body) => w.Admin.SendAsync(HttpMethod.Put, $"/api/integrations/people/{person}", body);

    [Fact]
    public async Task An_admin_unlinks_a_person_and_an_empty_body_or_unlink_false_changes_nothing()
    {
        var (w, hidden, _) = await WorldAsync();
        var member = await RemoteKit.MemberAsync(api, w.Admin, "lnk-unlink");
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w, hidden, new { userId = member.Id })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w, hidden, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w, hidden, new { unlink = false })).StatusCode);
        Assert.Equal(member.Id, await UserOfAsync(hidden));

        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w, hidden, new { unlink = true })).StatusCode);

        Assert.Null(await UserOfAsync(hidden));
        // Each change is audited with the member it now stands for (F-070): the link with the member's id, the unlink with none.
        await using var db = api.Db();
        var audit = await db.AuditEvents.AsNoTracking().Where(a => a.Action == "integration.person_linked" && a.TargetId == hidden.ToString())
            .OrderBy(a => a.Id).ToListAsync();
        var users = audit.Select(a => a.Detail?.RootElement.TryGetProperty("userId", out var u) == true ? u.ToString() : "absent").ToList();
        Assert.Equal(member.Id.ToString(), users[0]);
        Assert.Equal("", users[^1]); // JSON null
    }

    [Fact]
    public async Task A_call_that_changes_nothing_leaves_no_audit_row_and_a_change_of_the_address_is_audited_without_the_address()
    {
        var (w, hidden, _) = await WorldAsync();
        var member = await RemoteKit.MemberAsync(api, w.Admin, "lnk-audit");
        async Task<List<AuditEvent>> AuditAsync()
        {
            await using var db = api.Db();
            return await db.AuditEvents.AsNoTracking().Where(a => a.Action == "integration.person_linked" && a.TargetId == hidden.ToString()).OrderBy(a => a.Id).ToListAsync();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w, hidden, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w, hidden, new { unlink = true })).StatusCode); // already unlinked
        Assert.Empty(await AuditAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w, hidden, new { userId = member.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w, hidden, new { userId = member.Id })).StatusCode); // the same link again
        Assert.Single(await AuditAsync());

        var email = $"aud+{Guid.NewGuid():N}@gmail.com";
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w, hidden, new { email })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w, hidden, new { email })).StatusCode); // the same address again
        var rows = await AuditAsync();
        Assert.Equal(2, rows.Count);
        var detail = rows[^1].Detail!.RootElement;
        Assert.True(detail.GetProperty("emailChanged").GetBoolean());
        Assert.Equal(member.Id, detail.GetProperty("userId").GetGuid());
        Assert.DoesNotContain(email, detail.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(member.Id, rows[0].Detail!.RootElement.GetProperty("userId").GetGuid());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, rows[0].Detail!.RootElement.GetProperty("previousUserId").ValueKind);
    }

    [Fact]
    public async Task Unlinking_and_linking_in_one_call_is_refused_and_changes_nothing()
    {
        var (w, hidden, _) = await WorldAsync();
        var member = await RemoteKit.MemberAsync(api, w.Admin, "lnk-both");

        var refused = await PutAsync(w, hidden, new { unlink = true, userId = member.Id });

        Assert.Equal(("unlink", "invalid_link"), await ProjectKit.ProblemAsync(refused));
        Assert.Null(await UserOfAsync(hidden));
    }

    [Fact]
    public async Task A_member_linked_to_one_person_cannot_be_linked_to_a_second_until_unlinked_there()
    {
        var (w, hidden, other) = await WorldAsync();
        var member = await RemoteKit.MemberAsync(api, w.Admin, "lnk-twice");
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w, hidden, new { userId = member.Id })).StatusCode);

        Assert.Equal(("userId", "already_linked"), await ProjectKit.ProblemAsync(await PutAsync(w, other, new { userId = member.Id })));
        Assert.Null(await UserOfAsync(other));
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w, hidden, new { userId = member.Id })).StatusCode); // the same link again is fine

        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w, hidden, new { unlink = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w, other, new { userId = member.Id })).StatusCode);
        Assert.Equal((null, member.Id), (await UserOfAsync(hidden), await UserOfAsync(other)));
    }

    [Fact]
    public async Task A_pass_does_not_give_a_member_who_is_linked_by_hand_a_second_person_by_e_mail()
    {
        var (w, hidden, _) = await WorldAsync();
        var member = await RemoteKit.MemberAsync(api, w.Admin, "lnk-auto");
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w, hidden, new { userId = member.Id })).StatusCode);
        w.Site.Spaces.Single(s => s.Key == "LNK").People.Add(new FakePerson("p-by-mail", "By Mail", member.Email));

        await JiraKit.PassAsync(w);

        await using var db = api.Db();
        var byMail = await db.IntegrationPeople.AsNoTracking().SingleAsync(p => p.ConnectionId == w.ConnectionId && p.ExternalAccountId == "p-by-mail");
        Assert.Null(byMail.UserId);
        Assert.Equal(member.Id, await UserOfAsync(hidden));
    }

    [Fact]
    public async Task A_person_whose_e_mail_an_admin_typed_is_linked_on_the_next_pass_after_that_person_accepted_the_invitation()
    {
        var (w, hidden, _) = await WorldAsync();
        var invitee = await api.NewClient().SignedUpAsync("lnk-typed");
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w, hidden, new { email = "  " + invitee.Email.ToUpperInvariant() + " " })).StatusCode);
        (await w.Admin.PostAsync($"/api/workspaces/{w.WorkspaceId}/invitations", new { email = invitee.Email, role = "viewer" })).EnsureSuccessStatusCode();
        (await invitee.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(invitee.Email) })).EnsureSuccessStatusCode();
        Assert.Null(await UserOfAsync(hidden)); // Jira still shows no e-mail for this person

        await JiraKit.PassAsync(w);

        Assert.Equal(invitee.Id, await UserOfAsync(hidden));
    }

    [Fact]
    public async Task A_second_person_row_of_one_member_is_refused_by_the_database_and_the_tool_account_is_the_one_linked()
    {
        var (w, hidden, other) = await WorldAsync();
        var member = await RemoteKit.MemberAsync(api, w.Admin, "lnk-order");
        await using (var db = api.Db())
        {
            // Two rows of one member could exist before a member stood for one person; the index ux_integration_people_user makes it impossible.
            await db.IntegrationPeople.Where(p => p.Id == other).ExecuteUpdateAsync(p => p.SetProperty(x => x.UserId, member.Id));
            var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                db.IntegrationPeople.Where(p => p.Id == hidden).ExecuteUpdateAsync(p => p.SetProperty(x => x.UserId, member.Id)));
            Assert.Equal("ux_integration_people_user", refused.ConstraintName);
        }

        using var scope = api.Services.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<MirrorGate>();
        var seen = new List<string?>();
        for (var i = 0; i < 5; i++) seen.Add(await gate.AccountOfAsync(w.ConnectionId, member.Id, CancellationToken.None));

        Assert.All(seen, s => Assert.Equal("p-other", s));
    }
}
