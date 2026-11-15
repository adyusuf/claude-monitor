using System.Net;
using Podium.Api.Data;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task The_tool_account_of_a_member_with_two_person_rows_from_before_is_always_the_first_seen()
    {
        var (w, hidden, other) = await WorldAsync();
        var member = await RemoteKit.MemberAsync(api, w.Admin, "lnk-order");
        var now = api.Clock.GetUtcNow();
        await using (var db = api.Db())
        {
            // Two rows of one member, as the people table could hold them before a member stood for one person: the later created row was seen first.
            await db.IntegrationPeople.Where(p => p.Id == hidden).ExecuteUpdateAsync(p => p.SetProperty(x => x.UserId, member.Id).SetProperty(x => x.FirstSeenAt, now));
            await db.IntegrationPeople.Where(p => p.Id == other).ExecuteUpdateAsync(p => p.SetProperty(x => x.UserId, member.Id).SetProperty(x => x.FirstSeenAt, now.AddDays(-1)));
        }

        using var scope = api.Services.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<MirrorGate>();
        var seen = new List<string?>();
        for (var i = 0; i < 5; i++) seen.Add(await gate.AccountOfAsync(w.ConnectionId, member.Id, CancellationToken.None));

        Assert.All(seen, s => Assert.Equal("p-other", s));
    }
}
