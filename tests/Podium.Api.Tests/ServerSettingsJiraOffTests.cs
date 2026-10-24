using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Podium.Api.Tests;

/// <summary>What the running services do while no Jira app is in force (ADR-0017): the reconciler idles, Jira sign-in has no app, and a missing table does not stop the start.</summary>
[Collection(ApiGroup.Name)]
public sealed class ServerSettingsJiraOffTests(ApiFactory api)
{
    private ApiConfig Config => api.Services.GetRequiredService<ApiConfig>();

    private IServiceScopeFactory Scopes => api.Services.GetRequiredService<IServiceScopeFactory>();

    private Reconciler Reconciler(IntegrationSettings settings) => new(Scopes, Config, settings, api.Clock, NullLogger<Reconciler>.Instance);

    private async Task<int> PassesAsync(JiraWorld w)
    {
        await using var db = api.Db();
        return await db.IntegrationSyncRuns.CountAsync(r => r.ConnectionId == w.ConnectionId && r.Kind == SyncKinds.Pass);
    }

    [Fact]
    public async Task Without_a_jira_app_the_reconciler_passes_nothing_but_still_cleans_up_and_with_one_it_passes_the_same_connection()
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space("OFF").Issue("One"));
        var now = api.Clock.GetUtcNow();
        await using (var db = api.Db())
        {
            // The other tests' connections are put aside, so that only this one is looked at.
            await db.IntegrationConnections.Where(c => c.Id != w.ConnectionId && c.Status == ConnectionStatuses.Active)
                .ExecuteUpdateAsync(c => c.SetProperty(x => x.Status, ConnectionStatuses.Disconnected));
            db.IntegrationWebhookReceipts.Add(new IntegrationWebhookReceipt
            {
                ConnectionId = w.ConnectionId,
                DeliveryId = "old-off",
                ReceivedAt = now - Integrations.Reconciler.ReceiptKeep - TimeSpan.FromMinutes(1),
            });
            db.IntegrationOAuthStates.Add(new IntegrationOAuthState
            {
                StateHash = "expired-off-" + Guid.NewGuid(),
                UserId = w.Admin.Id,
                WorkspaceId = w.WorkspaceId,
                Verifier = "x",
                CreatedAt = now.AddMinutes(-20),
                ExpiresAt = now.AddMinutes(-10),
            });
            await db.SaveChangesAsync();
        }

        var noApp = ConfigKit.Settings(ConfigKit.Dev());
        Assert.False(noApp.JiraAvailable);
        await Reconciler(noApp).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, await PassesAsync(w));
        Assert.Null((await JiraKit.ConnectionAsync(w)).LastPassAt);
        await using (var db = api.Db())
        {
            Assert.False(await db.IntegrationWebhookReceipts.AnyAsync(r => r.ConnectionId == w.ConnectionId && r.DeliveryId == "old-off"));
            Assert.False(await db.IntegrationOAuthStates.AnyAsync(s => s.UserId == w.Admin.Id));
        }

        var running = api.Services.GetRequiredService<IntegrationSettings>();
        Assert.True(running.JiraAvailable);
        await Reconciler(running).RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, await PassesAsync(w));
    }

    [Fact]
    public async Task Jira_sign_in_has_no_app_to_use_until_one_is_set()
    {
        var clients = api.Services.GetRequiredService<IHttpClientFactory>();
        var without = new JiraOAuth(clients, Config, ConfigKit.Settings(ConfigKit.Dev()));
        var with = new JiraOAuth(clients, Config, api.Services.GetRequiredService<IntegrationSettings>());

        var error = Assert.Throws<TrackerException>(() => without.AuthorizeUrl("state", JiraOAuth.NewVerifier()));
        Assert.Equal(TrackerException.Codes.Unavailable, error.Code);
        await Assert.ThrowsAsync<TrackerException>(() => without.ExchangeAsync("code", "verifier", CancellationToken.None));
        await Assert.ThrowsAsync<TrackerException>(() => without.RefreshAsync("refresh", CancellationToken.None));
        var url = with.AuthorizeUrl("state", JiraOAuth.NewVerifier());
        Assert.Equal(FakeJira.ClientId, JiraKit.Param(url, "client_id"));
        Assert.Equal(ApiFactory.Origin + JiraOAuth.CallbackPath, JiraKit.Param(url, "redirect_uri"));
        Assert.Equal(ApiFactory.Origin + "/api/integrations/jira/callback", with.RedirectUri);
    }

    [Fact]
    public async Task A_database_without_the_table_is_swallowed_by_the_loader_and_the_environments_values_stay_in_force()
    {
        const string empty = "rf_empty_db";
        await using (var db = api.Db())
        {
            await db.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS {empty} WITH (FORCE)");
            await db.Database.ExecuteSqlRawAsync($"CREATE DATABASE {empty}");
        }

        var connection = new Npgsql.NpgsqlConnectionStringBuilder(Config.DatabaseUrl) { Database = empty, Pooling = false }.ConnectionString;
        var services = new ServiceCollection();
        services.AddDbContext<PodiumDb>(o => o.UseNpgsql(connection));
        await using var provider = services.BuildServiceProvider();
        var settings = ConfigKit.Settings(ConfigKit.Dev(("AIPOD_SMTP_HOST", "env.example.net")), provider.GetRequiredService<IServiceScopeFactory>());
        var lines = new List<string>();
        using var loader = new IntegrationSettingsLoader(settings, new CapturingLog<IntegrationSettingsLoader>(lines));

        await loader.StartAsync(CancellationToken.None);
        await loader.StopAsync(CancellationToken.None);

        Assert.Equal("env.example.net", settings.Smtp.Host);
        Assert.Contains("PostgresException", Assert.Single(lines), StringComparison.Ordinal);
        await using var cleanup = api.Db();
        await cleanup.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS {empty} WITH (FORCE)");
    }

    private sealed class CapturingLog<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception));
    }
}
