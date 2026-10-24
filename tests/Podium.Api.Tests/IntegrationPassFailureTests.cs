using System.Security.Cryptography;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Podium.Api.Tests;

/// <summary>
/// A pass that fails for a reason that is not the tracker's (here: a stored token that cannot be opened) closes its own run as failed, and the loop goes on
/// with the next connection and the clean-up; a shutdown is still not swallowed.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationPassFailureTests(ApiFactory api)
{
    private Reconciler ReconcilerOn(IServiceProvider services, ListLogger<Reconciler> log) => new(services.GetRequiredService<IServiceScopeFactory>(),
        services.GetRequiredService<ApiConfig>(), services.GetRequiredService<IntegrationSettings>(), api.Clock, log);

    /// <summary>A connected site with one imported space; when broken, its stored tokens cannot be opened (not a tracker failure).</summary>
    private async Task<JiraWorld> WorldAsync(string space, bool broken)
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space(space).Issue("One"));
        await JiraKit.ImportAsync(w, space);
        if (broken)
        {
            await using var db = api.Db();
            await db.IntegrationAccounts.Where(a => a.ConnectionId == w.ConnectionId)
                .ExecuteUpdateAsync(a => a.SetProperty(x => x.AccessToken, "AAAA").SetProperty(x => x.RefreshToken, "AAAA"));
        }

        return w;
    }

    /// <summary>The connections other tests made are put aside, so that the loop only has these to look at.</summary>
    private async Task OnlyAsync(params JiraWorld[] keep)
    {
        var ids = keep.Select(k => k.ConnectionId).ToList();
        await using var db = api.Db();
        await db.IntegrationConnections.Where(c => !ids.Contains(c.Id) && c.Status == ConnectionStatuses.Active)
            .ExecuteUpdateAsync(c => c.SetProperty(x => x.Status, ConnectionStatuses.Disconnected));
    }

    private async Task<List<IntegrationSyncRun>> RunsAsync(JiraWorld w)
    {
        await using var db = api.Db();
        return await db.IntegrationSyncRuns.AsNoTracking().Where(r => r.ConnectionId == w.ConnectionId && r.Kind == SyncKinds.Pass).OrderBy(r => r.Id).ToListAsync();
    }

    private async Task<(string Receipt, string State)> StaleRowsAsync(JiraWorld w)
    {
        var now = api.Clock.GetUtcNow();
        var receipt = "old-" + Guid.NewGuid();
        var state = "expired-" + Guid.NewGuid();
        await using var db = api.Db();
        db.IntegrationWebhookReceipts.Add(new IntegrationWebhookReceipt { ConnectionId = w.ConnectionId, DeliveryId = receipt, ReceivedAt = now - Reconciler.ReceiptKeep - TimeSpan.FromMinutes(1) });
        db.IntegrationOAuthStates.Add(new IntegrationOAuthState { StateHash = state, UserId = w.Admin.Id, WorkspaceId = w.WorkspaceId, Verifier = "x", CreatedAt = now.AddMinutes(-20), ExpiresAt = now.AddMinutes(-10) });
        await db.SaveChangesAsync();
        return (receipt, state);
    }

    private async Task<(bool Receipt, bool State)> StaleStillThereAsync((string Receipt, string State) rows)
    {
        await using var db = api.Db();
        return (await db.IntegrationWebhookReceipts.AnyAsync(r => r.DeliveryId == rows.Receipt), await db.IntegrationOAuthStates.AnyAsync(s => s.StateHash == rows.State));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_connection_whose_pass_fails_for_its_own_reasons_does_not_stop_the_other_nor_the_cleanup_in_either_order(bool failingFirst)
    {
        var (a, b) = failingFirst
            ? (await WorldAsync("PFA", broken: true), await WorldAsync("PFB", broken: false))
            : (await WorldAsync("PFC", broken: false), await WorldAsync("PFD", broken: true));
        var (bad, good) = failingFirst ? (a, b) : (b, a);
        await OnlyAsync(a, b);
        var stale = await StaleRowsAsync(good);
        var log = new ListLogger<Reconciler>();
        using var reconciler = ReconcilerOn(api.Services, log);

        await reconciler.RunOnceAsync(CancellationToken.None);

        var ok = Assert.Single(await RunsAsync(good));
        Assert.Equal(SyncStatuses.Ok, ok.Status);
        Assert.Equal(api.Clock.GetUtcNow(), (await JiraKit.ConnectionAsync(good)).LastPassAt);
        var failed = Assert.Single(await RunsAsync(bad));
        Assert.Equal((SyncStatuses.Failed, SyncService.InternalError, true), (failed.Status, failed.Error, failed.EndedAt is not null));
        Assert.Null((await JiraKit.ConnectionAsync(bad)).LastPassAt);
        await using (var db = api.Db())
        {
            Assert.False(await db.IntegrationSyncRuns.AnyAsync(r => r.Status == SyncStatuses.Running && (r.ConnectionId == a.ConnectionId || r.ConnectionId == b.ConnectionId)));
        }

        Assert.Equal((false, false), await StaleStillThereAsync(stale)); // the clean-up ran although a pass failed
        var line = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains(bad.ConnectionId.ToString(), line.Text, StringComparison.Ordinal);
        Assert.Contains(nameof(CryptographicException), line.Text, StringComparison.Ordinal);

        // The next look tries the failing connection again, and leaves the one that is up to date alone.
        await reconciler.RunOnceAsync(CancellationToken.None);

        Assert.Equal([SyncStatuses.Failed, SyncStatuses.Failed], (await RunsAsync(bad)).Select(r => r.Status));
        Assert.Single(await RunsAsync(good));
        Assert.Equal(2, log.Entries.Count);
    }

    [Fact]
    public async Task A_pass_called_directly_closes_its_run_as_failed_and_goes_on_to_throw_the_error()
    {
        var w = await WorldAsync("PFE", broken: true);
        await using var scope = api.Services.CreateAsyncScope();
        var connection = await scope.ServiceProvider.GetRequiredService<PodiumDb>().IntegrationConnections.AsNoTracking().FirstAsync(c => c.Id == w.ConnectionId);

        await Assert.ThrowsAsync<CryptographicException>(() => scope.ServiceProvider.GetRequiredService<SyncService>().PassAsync(connection, CancellationToken.None));

        var run = Assert.Single(await RunsAsync(w));
        Assert.Equal((SyncStatuses.Failed, SyncService.InternalError, true), (run.Status, run.Error, run.EndedAt is not null));
        Assert.Equal("internal_error", SyncService.InternalError);
    }

    [Fact]
    public async Task A_shutdown_in_the_middle_of_a_pass_still_leaves_it_as_a_cancellation_from_the_pass_and_from_the_loop()
    {
        var w = await WorldAsync("PFF", broken: false);
        await OnlyAsync(w);
        var fault = new CommandFault("FROM integration_accounts");
        await using var host = HostKit.WithInterceptor(api, fault);
        var log = new ListLogger<Reconciler>();
        using var reconciler = ReconcilerOn(host.Services, log);

        using (var stop = new CancellationTokenSource())
        {
            fault.Cancel = stop;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconciler.RunOnceAsync(stop.Token));
        }

        Assert.Empty(log.Entries); // not logged as a failure
        await using var scope = host.Services.CreateAsyncScope();
        var connection = await scope.ServiceProvider.GetRequiredService<PodiumDb>().IntegrationConnections.AsNoTracking().FirstAsync(c => c.Id == w.ConnectionId);
        using var stopAgain = new CancellationTokenSource();
        fault.Cancel = stopAgain;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<SyncService>().PassAsync(connection, stopAgain.Token));
        Assert.DoesNotContain(await RunsAsync(w), r => r.Error == SyncService.InternalError); // a shutdown is not an internal failure of the run
    }

    [Fact]
    public async Task A_cleanup_that_fails_is_logged_and_does_not_hide_the_result_of_the_passes()
    {
        var w = await WorldAsync("PFG", broken: false);
        await OnlyAsync(w);
        var stale = await StaleRowsAsync(w);
        var fault = new CommandFault("DELETE FROM integration_webhook_receipts") { Error = new DatabaseDownException() };
        await using var host = HostKit.WithInterceptor(api, fault);
        var log = new ListLogger<Reconciler>();
        using var reconciler = ReconcilerOn(host.Services, log);

        await reconciler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(SyncStatuses.Ok, Assert.Single(await RunsAsync(w)).Status);
        Assert.Equal(api.Clock.GetUtcNow(), (await JiraKit.ConnectionAsync(w)).LastPassAt);
        var line = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("clean-up", line.Text, StringComparison.Ordinal);
        Assert.Contains(nameof(DatabaseDownException), line.Text, StringComparison.Ordinal);
        Assert.Equal((true, true), await StaleStillThereAsync(stale)); // the clean-up really did fail
        fault.Error = null;

        await reconciler.RunOnceAsync(CancellationToken.None); // the next look cleans up

        Assert.Equal((false, false), await StaleStillThereAsync(stale));
    }
}
