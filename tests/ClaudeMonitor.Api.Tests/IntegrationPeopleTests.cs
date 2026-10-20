using System.Net;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Reading and correcting the people of a connection (ADR-0015 §8): admin only, and an admin can link a person or type the missing e-mail.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationPeopleTests(ApiFactory api)
{
    private async Task<(JiraWorld World, Guid Hidden, Guid Visible)> WorldAsync()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var space = w.Site.Space("PEO");
        space.People.AddRange([new FakePerson("p-hidden", "Hidden Person"), new FakePerson("p-visible", "Visible Person", Emails.New("peo-visible"))]);
        await JiraKit.ImportAsync(w, "PEO");
        await using var db = api.Db();
        var people = await db.IntegrationPeople.AsNoTracking().Where(p => p.ConnectionId == w.ConnectionId).ToDictionaryAsync(p => p.ExternalAccountId, p => p.Id);
        return (w, people["p-hidden"], people["p-visible"]);
    }

    private async Task<IntegrationPerson> PersonAsync(Guid id)
    {
        await using var db = api.Db();
        return await db.IntegrationPeople.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    [Fact]
    public async Task The_list_of_people_is_for_admins_only()
    {
        var (w, _, _) = await WorldAsync();
        var member = await RemoteKit.MemberAsync(api, w.Admin, "peo-member");
        var viewer = await RemoteKit.MemberAsync(api, w.Admin, "peo-viewer", "viewer");
        var stranger = await api.NewClient().SignedUpAsync("peo-stranger");

        foreach (var user in new[] { member, viewer, stranger })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync($"{w.Base}/people")).StatusCode);
        }

        var admin = (await w.Admin.GetJsonAsync($"{w.Base}/people")).EnumerateArray().ToList();
        Assert.Equal(["Hidden Person", "Visible Person"], admin.Select(p => p.GetProperty("displayName").GetString()));
        Assert.Equal((false, true), (admin[0].GetProperty("invited").GetBoolean(), admin[0].GetProperty("active").GetBoolean()));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, admin[0].GetProperty("email").ValueKind);
    }

    [Fact]
    public async Task An_admin_links_a_person_to_a_member_of_the_workspace()
    {
        var (w, hidden, _) = await WorldAsync();
        var member = await RemoteKit.MemberAsync(api, w.Admin, "peo-link");

        Assert.Equal(HttpStatusCode.NoContent, (await w.Admin.SendAsync(HttpMethod.Put, $"/api/integrations/people/{hidden}", new { userId = member.Id })).StatusCode);

        Assert.Equal(member.Id, (await PersonAsync(hidden)).UserId);
        var shown = (await w.Admin.GetJsonAsync($"{w.Base}/people")).EnumerateArray().Single(p => p.GetProperty("displayName").GetString() == "Hidden Person");
        Assert.Equal(("Test Person", member.Id), (shown.GetProperty("userName").GetString(), shown.GetProperty("userId").GetGuid()));
        await using var db = api.Db();
        Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.WorkspaceId == w.WorkspaceId && a.Action == "integration.person_linked" && a.TargetId == hidden.ToString()));
    }

    [Fact]
    public async Task A_person_can_only_be_linked_to_somebody_who_is_a_member_now()
    {
        var (w, hidden, _) = await WorldAsync();
        var gone = await RemoteKit.MemberAsync(api, w.Admin, "peo-gone");
        var stranger = await api.NewClient().SignedUpAsync("peo-outsider");
        (await w.Admin.SendAsync(HttpMethod.Delete, $"/api/workspaces/{w.WorkspaceId}/members/{gone.Id}")).EnsureSuccessStatusCode();

        foreach (var who in new[] { stranger.Id, gone.Id, Guid.NewGuid() })
        {
            var refused = await w.Admin.SendAsync(HttpMethod.Put, $"/api/integrations/people/{hidden}", new { userId = who });
            Assert.Equal(("userId", "not_a_member"), await ProjectKit.ProblemAsync(refused));
        }

        Assert.Null((await PersonAsync(hidden)).UserId);
    }

    [Fact]
    public async Task An_admin_types_the_missing_e_mail_and_the_next_run_invites_that_person()
    {
        var (w, hidden, _) = await WorldAsync();
        (await w.Admin.SendAsync(HttpMethod.Put, w.Base, new { autoInvite = true })).EnsureSuccessStatusCode();
        var address = Emails.New("peo-typed");

        Assert.Equal(HttpStatusCode.NoContent, (await w.Admin.SendAsync(HttpMethod.Put, $"/api/integrations/people/{hidden}", new { email = "  " + address.ToUpperInvariant() + " " })).StatusCode);

        var person = await PersonAsync(hidden);
        Assert.Equal((address.ToUpperInvariant(), address.ToLowerInvariant()), (person.Email, person.EmailNormalized));
        await JiraKit.PassAsync(w);
        Assert.Equal(1, api.Mail.Sent.Count(m => string.Equals(m.To, address, StringComparison.OrdinalIgnoreCase)));
        Assert.NotNull((await PersonAsync(hidden)).InvitedAt);
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("two@example.invalid, three@example.invalid")]
    public async Task A_malformed_e_mail_is_refused_and_changes_nothing(string email)
    {
        var (w, hidden, _) = await WorldAsync();

        var refused = await w.Admin.SendAsync(HttpMethod.Put, $"/api/integrations/people/{hidden}", new { email });

        Assert.Equal(("email", "invalid_email"), await ProjectKit.ProblemAsync(refused));
        Assert.Null((await PersonAsync(hidden)).Email);
    }

    [Fact]
    public async Task Only_an_admin_of_that_connections_workspace_may_change_a_person()
    {
        var (w, hidden, visible) = await WorldAsync();
        var member = await RemoteKit.MemberAsync(api, w.Admin, "peo-nope");
        var (other, _, _) = await WorldAsync();

        foreach (var user in new[] { member, other.Admin })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await user.SendAsync(HttpMethod.Put, $"/api/integrations/people/{hidden}", new { userId = member.Id })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await w.Admin.SendAsync(HttpMethod.Put, $"/api/integrations/people/{Guid.NewGuid()}", new { userId = member.Id })).StatusCode);
        Assert.Null((await PersonAsync(hidden)).UserId);
        Assert.Equal(HttpStatusCode.NoContent, (await w.Admin.SendAsync(HttpMethod.Put, $"/api/integrations/people/{visible}", new { })).StatusCode); // nothing to change is not an error
    }

    [Fact]
    public async Task The_people_of_a_disconnected_connection_can_no_longer_be_changed()
    {
        var (w, hidden, _) = await WorldAsync();
        (await w.Admin.SendAsync(HttpMethod.Delete, w.Base)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await w.Admin.SendAsync(HttpMethod.Put, $"/api/integrations/people/{hidden}", new { email = "x@example.invalid" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Admin.Http.GetAsync($"{w.Base}/people")).StatusCode);
    }
}
