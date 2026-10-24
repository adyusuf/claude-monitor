using Podium.Api.Data;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Podium.Api.Tests;

/// <summary>
/// The two loops that now run on every server (ADR-0017) while the database may be down: a failure is logged as a warning that names only the
/// kind of error and never stops the loop (a BackgroundService that throws stops the host); a shutdown is never swallowed. No host, no database.
/// </summary>
public sealed class IntegrationLoopFailureTests
{
    private const string UnreachableDb = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=unreachable-pw;Timeout=3";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private static Reconciler ReconcilerOver(IServiceScopeFactory scopes, ListLogger<Reconciler> log) =>
        new(scopes, ConfigKit.Dev(), ConfigKit.Settings(ConfigKit.Dev()), TimeProvider.System, log);

    private static SyncWorker WorkerOver(IServiceScopeFactory scopes, SyncInbox inbox, ListLogger<SyncWorker> log) => new(scopes, inbox, log);

    private static SyncRequest Request(string reference) => new(Guid.NewGuid(), SyncTarget.Issue, reference);

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        using var timeout = new CancellationTokenSource(Wait);
        while (!condition())
        {
            if (timeout.IsCancellationRequested) Assert.Fail("timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    // ---- the Reconciler -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_raw_database_error_in_the_reconciler_is_logged_as_a_warning_and_does_not_escape()
    {
        var log = new ListLogger<Reconciler>();
        using var reconciler = ReconcilerOver(new FailingScopes(new DatabaseDownException()), log);

        await reconciler.RunOnceAsync(CancellationToken.None);

        var (level, text) = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains(nameof(DatabaseDownException), text, StringComparison.Ordinal);
        Assert.DoesNotContain("the database is down", text, StringComparison.Ordinal); // the kind of error, not its message
    }

    [Fact]
    public async Task A_database_that_cannot_be_reached_fails_the_cleanup_and_that_is_only_logged()
    {
        var services = new ServiceCollection();
        services.AddDbContext<PodiumDb>(o => o.UseNpgsql(UnreachableDb));
        using var provider = services.BuildServiceProvider();
        var log = new ListLogger<Reconciler>();
        using var reconciler = ReconcilerOver(provider.GetRequiredService<IServiceScopeFactory>(), log);

        await reconciler.RunOnceAsync(CancellationToken.None); // no Jira app: only the clean-up runs, and it meets the missing server

        var (level, text) = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.EndsWith("Exception", text, StringComparison.Ordinal); // "Integration reconciliation failed: <type>"
        Assert.DoesNotContain("unreachable-pw", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shutdown_still_leaves_the_reconciler_as_a_cancellation_and_logs_nothing()
    {
        var log = new ListLogger<Reconciler>();
        using var reconciler = ReconcilerOver(new FailingScopes(new OperationCanceledException()), log);

        await Assert.ThrowsAsync<OperationCanceledException>(() => reconciler.RunOnceAsync(CancellationToken.None));

        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task A_run_asked_to_stop_while_it_meets_the_database_ends_as_a_cancellation_not_as_a_logged_failure()
    {
        var services = new ServiceCollection();
        services.AddDbContext<PodiumDb>(o => o.UseNpgsql(UnreachableDb));
        using var provider = services.BuildServiceProvider();
        var log = new ListLogger<Reconciler>();
        using var reconciler = ReconcilerOver(provider.GetRequiredService<IServiceScopeFactory>(), log);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconciler.RunOnceAsync(new CancellationToken(canceled: true)));

        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task As_a_hosted_service_the_reconciler_survives_a_database_that_is_down_and_stops_cleanly()
    {
        var log = new ListLogger<Reconciler>();
        using var reconciler = ReconcilerOver(new FailingScopes(new DatabaseDownException()), log);

        await reconciler.StartAsync(CancellationToken.None);
        await UntilAsync(() => log.Entries.Count == 1, "the first look at the database");

        Assert.False(reconciler.ExecuteTask!.IsCompleted); // the loop waits for its next tick; it did not die on the error
        await reconciler.StopAsync(CancellationToken.None);
        Assert.True(reconciler.ExecuteTask.IsCompletedSuccessfully);
    }

    // ---- the SyncWorker (HandleAsync is internal: it is reached the way the host reaches it, through the inbox) ---------

    [Fact]
    public async Task A_webhook_request_that_meets_a_database_error_is_logged_and_the_worker_takes_the_next_one()
    {
        var inbox = new SyncInbox();
        var log = new ListLogger<SyncWorker>();
        using var worker = WorkerOver(new FailingScopes(new DatabaseDownException()), inbox, log);
        await worker.StartAsync(CancellationToken.None);

        Assert.True(inbox.Enqueue(Request("AAA-1")));
        await UntilAsync(() => log.Entries.Count == 1, "the first request");
        Assert.False(worker.ExecuteTask!.IsCompleted);
        Assert.True(inbox.Enqueue(Request("AAA-2")));
        await UntilAsync(() => log.Entries.Count == 2, "the second request"); // the worker is still taking requests

        Assert.All(log.Entries, e =>
        {
            Assert.Equal(LogLevel.Warning, e.Level);
            Assert.Contains(nameof(DatabaseDownException), e.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("the database is down", e.Text, StringComparison.Ordinal);
        });
        await worker.StopAsync(CancellationToken.None);
        Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_webhook_request_against_an_unreachable_database_is_logged_not_thrown()
    {
        var services = new ServiceCollection();
        services.AddDbContext<PodiumDb>(o => o.UseNpgsql(UnreachableDb));
        using var provider = services.BuildServiceProvider();
        var inbox = new SyncInbox();
        var log = new ListLogger<SyncWorker>();
        using var worker = WorkerOver(provider.GetRequiredService<IServiceScopeFactory>(), inbox, log);
        await worker.StartAsync(CancellationToken.None);

        Assert.True(inbox.Enqueue(Request("BBB-1")));
        await UntilAsync(() => log.Entries.Count == 1, "the request");

        Assert.Equal(LogLevel.Warning, log.Entries[0].Level);
        Assert.EndsWith("Exception", log.Entries[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("unreachable-pw", log.Entries[0].Text, StringComparison.Ordinal);
        Assert.False(worker.ExecuteTask!.IsCompleted);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_cancellation_inside_a_request_still_ends_the_worker_as_a_cancellation_and_is_not_logged_as_a_failure()
    {
        var inbox = new SyncInbox();
        var log = new ListLogger<SyncWorker>();
        using var worker = WorkerOver(new FailingScopes(new OperationCanceledException()), inbox, log);
        await worker.StartAsync(CancellationToken.None);

        Assert.True(inbox.Enqueue(Request("CCC-1")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ExecuteTask!.WaitAsync(Wait));
        Assert.Empty(log.Entries);
    }
}
