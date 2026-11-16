using System.Data.Common;
using System.Net.Http.Json;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Integrations;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>
/// The unique indexes behind the link checks (F-068, F-080, F-337): a writer that loses a race to them gets the same clean answer as the check
/// in code gives, never a failure, and a pass that meets one keeps the rest of its people.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationLinkRaceTests(ApiFactory api)
{
    /// <summary>Holds the first two "is this Atlassian account linked by someone else" checks until both have arrived, so both pass before either saves.</summary>
    private sealed class MeetAtAccountCheck : DbCommandInterceptor
    {
        private readonly Barrier barrier = new(2);
        private int hits;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var text = command.CommandText;
            if (text.Contains("EXISTS", StringComparison.Ordinal) && text.Contains("FROM integration_accounts", StringComparison.Ordinal)
                && text.Contains("external_account_id", StringComparison.Ordinal) && Interlocked.Increment(ref hits) <= 2)
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10), cancellationToken), "the second sign-in never reached the account check");
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Just before the first save that links a person to <c>member</c>, runs <c>rival</c> once on the factory's own host (no interceptor).</summary>
    private sealed class RivalLink(Guid member, Func<Task> rival) : SaveChangesInterceptor
    {
        private int fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var linking = eventData.Context!.ChangeTracker.Entries<IntegrationPerson>()
                .Any(e => e.State is EntityState.Added or EntityState.Modified && e.Entity.UserId == member);
            if (linking && Interlocked.Exchange(ref fired, 1) == 0) await rival();
            return result;
        }
    }

    private async Task<(JiraWorld World, Dictionary<string, Guid> People)> WorldAsync()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var space = w.Site.Space("RCE");
        space.People.AddRange([new FakePerson("p-hidden", "Hidden Person"), new FakePerson("p-other", "Other Person")]);
        await JiraKit.ImportAsync(w, "RCE");
        await using var db = api.Db();
        return (w, await db.IntegrationPeople.AsNoTracking().Where(p => p.ConnectionId == w.ConnectionId).ToDictionaryAsync(p => p.ExternalAccountId, p => p.Id));
    }

    private async Task<Dictionary<string, Guid?>> LinksAsync(Guid connection)
    {
        await using var db = api.Db();
        return await db.IntegrationPeople.AsNoTracking().Where(p => p.ConnectionId == connection).ToDictionaryAsync(p => p.ExternalAccountId, p => p.UserId);
    }

    [Fact]
    public async Task Two_members_linking_the_same_atlassian_account_at_once_get_one_ok_and_one_account_in_use()
    {
        var w = await JiraKit.ConnectedAsync(api);
        var first = await RemoteKit.MemberAsync(api, w.Admin, "lrc-first");
        var second = await RemoteKit.MemberAsync(api, w.Admin, "lrc-second");
        var shared = api.Jira.Account("Shared", first.Email, w.Site);
        var urls = new List<string>();
        foreach (var user in new[] { first, second })
        {
            var started = await user.PostAsync($"{w.Base}/account/connect");
            urls.Add((await started.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("authorizeUrl").GetString()!);
        }

        await using var host = HostKit.WithInterceptor(api, new MeetAtAccountCheck());
        var clients = new[] { await HostKit.SignInAsync(api, host, first), await HostKit.SignInAsync(api, host, second) };

        var answers = await Task.WhenAll(JiraKit.ApproveAsync(clients[0], api.Jira, shared, urls[0]), JiraKit.ApproveAsync(clients[1], api.Jira, shared, urls[1]));

        Assert.Equal([IntegrationConnect.AccountInUse, IntegrationConnect.Ok], answers.Select(JiraKit.ResultOf).Order(StringComparer.Ordinal));
        await using var db = api.Db();
        var holders = await db.IntegrationAccounts.AsNoTracking().Where(a => a.ConnectionId == w.ConnectionId && a.ExternalAccountId == shared.Id).ToListAsync();
        Assert.Contains(Assert.Single(holders).UserId, new[] { first.Id, second.Id });
    }

    [Fact]
    public async Task A_manual_link_that_a_pass_beat_to_the_same_member_answers_already_linked_and_keeps_the_pass_link()
    {
        var (w, people) = await WorldAsync();
        var member = await RemoteKit.MemberAsync(api, w.Admin, "lrc-manual");
        var connection = await JiraKit.ConnectionAsync(w);
        // While the admin's link of "hidden" is on its way to the database, a pass matches "other" to the member by the address Jira shows.
        var rival = new RivalLink(member.Id, async () =>
        {
            await using var scope = api.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PeopleMirror>().UpsertAsync(connection,
                [new ExternalPerson("p-other", "Other Person", member.Email, true)], new MirrorCounts(), CancellationToken.None);
        });
        await using var host = HostKit.WithInterceptor(api, rival);
        var admin = await HostKit.SignInAsync(api, host, w.Admin);

        var response = await admin.SendAsync(HttpMethod.Put, $"/api/integrations/people/{people["p-hidden"]}", new { userId = member.Id });

        Assert.Equal(("userId", IntegrationImportEndpoints.AlreadyLinked), await ProjectKit.ProblemAsync(response));
        var links = await LinksAsync(w.ConnectionId);
        Assert.Equal((null, member.Id), (links["p-hidden"], links["p-other"]));
    }

    [Fact]
    public async Task A_pass_whose_automatic_link_meets_a_member_linked_meanwhile_stores_the_rest_of_its_people()
    {
        var (w, people) = await WorldAsync();
        var one = await RemoteKit.MemberAsync(api, w.Admin, "lrc-batch-one");
        var two = await RemoteKit.MemberAsync(api, w.Admin, "lrc-batch-two");
        var three = await RemoteKit.MemberAsync(api, w.Admin, "lrc-batch-three");
        var connection = await JiraKit.ConnectionAsync(w);
        // Just before the pass saves, an admin links member two to "other" by hand.
        var rival = new RivalLink(two.Id, async () =>
        {
            await using var db = api.Db();
            await db.IntegrationPeople.Where(p => p.Id == people["p-other"]).ExecuteUpdateAsync(p => p.SetProperty(x => x.UserId, two.Id));
        });
        await using var host = HostKit.WithInterceptor(api, rival);
        await using var scope = host.Services.CreateAsyncScope();
        var counts = new MirrorCounts();

        await scope.ServiceProvider.GetRequiredService<PeopleMirror>().UpsertAsync(connection,
        [
            new ExternalPerson("p-one", "Person One", one.Email, true),
            new ExternalPerson("p-two", "Person Two", two.Email, true),
            new ExternalPerson("p-three", "Person Three", three.Email, true),
        ], counts, CancellationToken.None);

        var links = await LinksAsync(w.ConnectionId);
        Assert.Equal((one.Id, null, three.Id, two.Id), (links["p-one"], links["p-two"], links["p-three"], links["p-other"]));
        Assert.Equal(3, counts.People); // every new person is stored, the one left unlinked too
        await using var check = api.Db();
        var audited = await check.AuditEvents.AsNoTracking().Where(a => a.WorkspaceId == w.WorkspaceId && a.Action == AuditActions.IntegrationPersonLinked).Select(a => a.TargetId).ToListAsync();
        var ids = await check.IntegrationPeople.AsNoTracking().Where(p => p.ConnectionId == w.ConnectionId).ToDictionaryAsync(p => p.ExternalAccountId, p => p.Id.ToString());
        Assert.Contains(ids["p-one"], audited);
        Assert.Contains(ids["p-three"], audited);
        Assert.DoesNotContain(ids["p-two"], audited); // the link left out leaves no audit row
    }
}
