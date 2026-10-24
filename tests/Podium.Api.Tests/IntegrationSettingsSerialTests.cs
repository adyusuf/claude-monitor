using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>Reading the saved rows is one read at a time (ADR-0017): an older read must not land after a newer one, and a failed read must not block the next.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationSettingsSerialTests(ApiFactory api) : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    public Task InitializeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    public Task DisposeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    private async Task SetHostAsync(TestUser by, string host)
    {
        await using var db = api.Db();
        var row = await db.ServerSettings.SingleOrDefaultAsync(s => s.Key == "smtp.host");
        if (row is null)
        {
            db.ServerSettings.Add(new ServerSetting { Key = "smtp.host", Value = host, UpdatedAt = api.Clock.GetUtcNow(), UpdatedBy = by.Id });
        }
        else
        {
            row.Value = host;
        }

        await db.SaveChangesAsync();
    }

    private static Task AllAsync(IntegrationSettings settings, int count) =>
        Task.WhenAll(Enumerable.Range(0, count).Select(_ => Task.Run(() => settings.RefreshAsync(CancellationToken.None)))).WaitAsync(Wait);

    [Fact]
    public async Task Refreshes_started_together_run_one_after_the_other_and_the_settings_end_as_the_latest_rows()
    {
        var by = await api.NewClient().SignedUpAsync("is-serial");
        var scopes = new CountingScopes(api.Services.GetRequiredService<IServiceScopeFactory>());
        using var settings = ConfigKit.Settings(ConfigKit.Dev(), scopes);
        await SetHostAsync(by, "first.example.net");

        await AllAsync(settings, 16);

        Assert.Equal("first.example.net", settings.Smtp.Host);
        Assert.Equal(16, scopes.Made); // every call read the table, none was skipped
        Assert.Equal(1, scopes.MostOpen); // and never two reads at once: without the lock sixteen scopes are open together

        await SetHostAsync(by, "second.example.net");
        await AllAsync(settings, 16);

        Assert.Equal("second.example.net", settings.Smtp.Host);
        Assert.Equal((32, 1), (scopes.Made, scopes.MostOpen));
    }

    [Fact]
    public async Task A_refresh_that_fails_gives_the_lock_back_so_the_next_one_runs()
    {
        var by = await api.NewClient().SignedUpAsync("is-fail");
        var scopes = new CountingScopes(api.Services.GetRequiredService<IServiceScopeFactory>());
        using var settings = ConfigKit.Settings(ConfigKit.Dev(), scopes);
        await SetHostAsync(by, "after.example.net");
        scopes.Fail = new DatabaseDownException();

        await Assert.ThrowsAsync<DatabaseDownException>(() => settings.RefreshAsync(CancellationToken.None));
        scopes.Fail = null;
        await settings.RefreshAsync(CancellationToken.None).WaitAsync(Wait); // would wait for ever if the failed read kept the lock

        Assert.Equal("after.example.net", settings.Smtp.Host);
    }

    [Fact]
    public async Task A_refresh_asked_to_stop_before_its_turn_throws_without_reading_and_the_next_one_still_runs()
    {
        var by = await api.NewClient().SignedUpAsync("is-cancel");
        var scopes = new CountingScopes(api.Services.GetRequiredService<IServiceScopeFactory>());
        using var settings = ConfigKit.Settings(ConfigKit.Dev(), scopes);
        await SetHostAsync(by, "kept.example.net");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settings.RefreshAsync(new CancellationToken(canceled: true)));
        Assert.Equal(0, scopes.Made); // it never began to read

        await settings.RefreshAsync(CancellationToken.None).WaitAsync(Wait);
        Assert.Equal("kept.example.net", settings.Smtp.Host);
    }
}
