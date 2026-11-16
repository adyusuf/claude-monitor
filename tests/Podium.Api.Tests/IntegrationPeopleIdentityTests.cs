using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>
/// F-180: a person of the tool is linked to a member by the e-mail identity key (trimmed, lower-cased), not by the search normalizer, so an
/// address that only looks like a member's (an accent, a dotless i) is a different mailbox and links to nobody.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationPeopleIdentityTests(ApiFactory api)
{
    [Fact]
    public async Task A_look_alike_address_links_to_no_member_and_the_same_address_in_other_case_still_links()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var jose = await RemoteKit.MemberAsync(api, w.Admin, "lla-jose");
        var mail = await RemoteKit.MemberAsync(api, w.Admin, "lla-mail");
        var cased = await RemoteKit.MemberAsync(api, w.Admin, "lla-case");
        var accented = jose.Email.Replace("+lla-jose", "+lla-josé", StringComparison.Ordinal);
        var dotless = mail.Email.Replace("mail", "maıl", StringComparison.Ordinal);
        Assert.NotEqual(jose.Email, accented);
        Assert.NotEqual(mail.Email, dotless);
        var connection = await JiraKit.ConnectionAsync(w);
        await using var scope = api.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<PeopleMirror>().UpsertAsync(connection,
        [
            new ExternalPerson("p-accented", "Accented Look-alike", accented, true),
            new ExternalPerson("p-dotless", "Dotless Look-alike", dotless, true),
            new ExternalPerson("p-cased", "Same Address", cased.Email.ToUpperInvariant(), true),
        ], new MirrorCounts(), CancellationToken.None);

        await using var db = api.Db();
        var links = await db.IntegrationPeople.AsNoTracking().Where(p => p.ConnectionId == w.ConnectionId).ToDictionaryAsync(p => p.ExternalAccountId, p => p.UserId);
        Assert.Equal((null, null, cased.Id), (links["p-accented"], links["p-dotless"], links["p-cased"]));
    }
}
