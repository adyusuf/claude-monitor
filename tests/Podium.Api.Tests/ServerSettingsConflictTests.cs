using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>Two first saves of the same setting at once (ADR-0017): the second insert meets the first one's key and answers 409 settings_conflict, never a 500.</summary>
[Collection(ApiGroup.Name)]
public sealed class ServerSettingsConflictTests(ApiFactory api) : IAsyncLifetime
{
    public Task InitializeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    public Task DisposeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    /// <summary>Just before the page's own insert of a new setting goes to the database, another writer inserts the same key (once).</summary>
    private sealed class RivalInsert(ApiFactory api, Guid by, string value) : SaveChangesInterceptor
    {
        private int fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var mine = eventData.Context!.ChangeTracker.Entries<ServerSetting>().FirstOrDefault(e => e.State == EntityState.Added);
            if (mine is not null && Interlocked.Exchange(ref fired, 1) == 0)
            {
                await using var other = api.Db(); // the factory's own host: it has no interceptor
                other.ServerSettings.Add(new ServerSetting { Key = mine.Entity.Key, Value = value, UpdatedAt = api.Clock.GetUtcNow(), UpdatedBy = by });
                await other.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
    }

    private async Task<ServerSetting?> RowAsync(string key)
    {
        await using var db = api.Db();
        return await db.ServerSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key);
    }

    [Fact]
    public async Task A_first_save_that_meets_the_same_key_inserted_meanwhile_answers_409_settings_conflict_and_saves_and_audits_nothing()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "sc-rival");
        var rival = new RivalInsert(api, admin.Id, "rival.example.net");
        await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.ConfigureDbContext<PodiumDb>(o => o.AddInterceptors(rival))));
        var client = await HostKit.SignInAsync(api, host, admin);

        var response = await ServerAdminKit.PutAsync(client, "smtp", new { host = "mine.example.net" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("settings_conflict", body.GetProperty("title").GetString());
        Assert.Equal("rival.example.net", (await RowAsync("smtp.host"))!.Value); // the other writer's row stands
        await using (var db = api.Db())
        {
            Assert.Equal(0, await db.AuditEvents.CountAsync(a => a.ActorUserId == admin.Id && a.Action == AuditActions.ServerSettingsChanged)); // the audit row went with the failed save
        }

        // Asking again meets a row now and replaces it.
        var again = await ServerAdminKit.PutAsync(client, "smtp", new { host = "mine.example.net" });

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("mine.example.net", (await RowAsync("smtp.host"))!.Value);
    }

    [Fact]
    public async Task Two_simultaneous_first_saves_of_the_same_setting_are_each_200_or_409_never_500_and_leave_one_row()
    {
        var admin = await ServerAdminKit.AdminAsync(api, "sc-race");

        for (var round = 0; round < 25; round++)
        {
            await ServerAdminKit.ResetSettingsAsync(api); // each round is a first save again
            var go = new TaskCompletionSource();
            var first = Task.Run(async () => { await go.Task; return await ServerAdminKit.PutAsync(admin, "smtp", new { host = "first.example.net" }); });
            var second = Task.Run(async () => { await go.Task; return await ServerAdminKit.PutAsync(admin, "smtp", new { host = "second.example.net" }); });
            go.SetResult();
            var both = await Task.WhenAll(first, second);

            Assert.All(both.Select(r => r.StatusCode), s => Assert.True(s is HttpStatusCode.OK or HttpStatusCode.Conflict, $"round {round}: {s}"));
            Assert.Contains(HttpStatusCode.OK, both.Select(r => r.StatusCode)); // at least one of them won
            await using var db = api.Db();
            var row = await db.ServerSettings.AsNoTracking().SingleAsync(s => s.Key == "smtp.host");
            Assert.Contains(row.Value, new[] { "first.example.net", "second.example.net" });
        }
    }
}
