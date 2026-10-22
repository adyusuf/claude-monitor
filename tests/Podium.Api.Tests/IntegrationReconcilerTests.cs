using System.Net;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Podium.Api.Tests;

/// <summary>The loop that runs a pass for every connection that is due (ADR-0015 §6) and cleans up behind it.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationReconcilerTests(ApiFactory api)
{
    private Reconciler Reconciler() => new(api.Services.GetRequiredService<IServiceScopeFactory>(), api.Services.GetRequiredService<ApiConfig>(), api.Clock, NullLogger<Reconciler>.Instance);

    /// <summary>The connections other tests made are put aside, so that the loop only has these to look at.</summary>
    private async Task OnlyAsync(params JiraWorld[] keep)
    {
        var ids = keep.Select(k => k.ConnectionId).ToList();
        await using var db = api.Db();
        await db.IntegrationConnections.Where(c => !ids.Contains(c.Id) && c.Status == ConnectionStatuses.Active)
            .ExecuteUpdateAsync(c => c.SetProperty(x => x.Status, ConnectionStatuses.Disconnected));
    }

    private async Task<int> PassesAsync(JiraWorld w)
    {
        await using var db = api.Db();
        return await db.IntegrationSyncRuns.CountAsync(r => r.ConnectionId == w.ConnectionId && r.Kind == SyncKinds.Pass);
    }

    [Fact]
    public async Task A_connection_that_was_never_passed_is_passed_and_is_left_alone_until_it_is_due_again()
    {
        FakeIssue? issue = null;
        var w = await JiraKit.ConnectedAsync(api, site => issue = site.Space("REC").Issue("Before"));
        await JiraKit.ImportAsync(w, "REC");
        await OnlyAsync(w);
        issue!.Summary = "After";
        api.Jira.Touch(issue);

        await Reconciler().RunOnceAsync(CancellationToken.None);
        var first = await JiraKit.ConnectionAsync(w);
        await Reconciler().RunOnceAsync(CancellationToken.None);

        Assert.Equal("After", (await JiraKit.IssueAsync(w, "REC-1")).Title);
        Assert.Equal(api.Clock.GetUtcNow(), first.LastPassAt);
        Assert.Equal(1, await PassesAsync(w)); // the second run found it up to date
        api.Clock.Advance(api.Services.GetRequiredService<ApiConfig>().IntegrationPassEvery + TimeSpan.FromSeconds(1));
        await Reconciler().RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, await PassesAsync(w));
    }

    [Fact]
    public async Task As_a_hosted_service_it_passes_at_once_when_started_and_stops_cleanly_when_asked()
    {
        FakeIssue? issue = null;
        var w = await JiraKit.ConnectedAsync(api, site => issue = site.Space("HST").Issue("Before"));
        await JiraKit.ImportAsync(w, "HST");
        await OnlyAsync(w);
        issue!.Summary = "After";
        api.Jira.Touch(issue);
        var reconciler = Reconciler();

        await reconciler.StartAsync(CancellationToken.None);
        await JiraKit.UntilAsync(async () => (await JiraKit.ConnectionAsync(w)).LastPassAt is not null, "the first pass");
        await reconciler.StopAsync(CancellationToken.None);

        Assert.Equal("After", (await JiraKit.IssueAsync(w, "HST-1")).Title);
        Assert.True(reconciler.ExecuteTask is { IsCompletedSuccessfully: true });
        reconciler.Dispose();
    }

    [Fact]
    public async Task A_connection_that_must_sign_in_again_or_is_disconnected_is_not_passed()
    {
        var again = await JiraKit.ConnectedAsync(api);
        var gone = await JiraKit.ConnectedAsync(api);
        (await gone.Admin.SendAsync(HttpMethod.Delete, gone.Base)).EnsureSuccessStatusCode();
        await using (var db = api.Db())
        {
            await db.IntegrationConnections.Where(c => c.Id == again.ConnectionId).ExecuteUpdateAsync(c => c.SetProperty(x => x.Status, ConnectionStatuses.Reauthorize));
        }

        await Reconciler().RunOnceAsync(CancellationToken.None);

        Assert.Equal((0, 0), (await PassesAsync(again), await PassesAsync(gone)));
    }

    [Fact]
    public async Task One_connection_that_fails_does_not_stop_the_others()
    {
        var broken = await JiraKit.ConnectedAsync(api, site => site.Space("BRK"));
        var fine = await JiraKit.ConnectedAsync(api, site => site.Space("FIN").Issue("One"));
        await JiraKit.ImportAsync(broken, "BRK");
        await JiraKit.ImportAsync(fine, "FIN");
        await OnlyAsync(broken, fine);
        api.Jira.Fail(broken.Site, "/rest/api/3/project/BRK", HttpStatusCode.ServiceUnavailable);

        await Reconciler().RunOnceAsync(CancellationToken.None);

        Assert.Equal("tracker_unavailable", (await JiraKit.ConnectionAsync(broken)).LastError);
        Assert.Null((await JiraKit.ConnectionAsync(broken)).LastPassAt);
        Assert.Equal(api.Clock.GetUtcNow(), (await JiraKit.ConnectionAsync(fine)).LastPassAt);
        // The one that failed is due again at the next look, and recovers.
        await Reconciler().RunOnceAsync(CancellationToken.None);
        Assert.Null((await JiraKit.ConnectionAsync(broken)).LastError);
    }

    [Fact]
    public async Task Receipts_older_than_a_week_and_sign_in_states_that_expired_are_removed_and_newer_ones_are_kept()
    {
        var w = await JiraKit.ConnectedAsync(api);
        await OnlyAsync(w);
        var now = api.Clock.GetUtcNow();
        await using (var db = api.Db())
        {
            db.IntegrationWebhookReceipts.AddRange(
                new IntegrationWebhookReceipt { ConnectionId = w.ConnectionId, DeliveryId = "old", ReceivedAt = now - Integrations.Reconciler.ReceiptKeep - TimeSpan.FromMinutes(1) },
                new IntegrationWebhookReceipt { ConnectionId = w.ConnectionId, DeliveryId = "fresh", ReceivedAt = now - Integrations.Reconciler.ReceiptKeep + TimeSpan.FromMinutes(1) });
            db.IntegrationOAuthStates.AddRange(
                new IntegrationOAuthState { StateHash = "expired-" + Guid.NewGuid(), UserId = w.Admin.Id, WorkspaceId = w.WorkspaceId, Verifier = "x", CreatedAt = now.AddMinutes(-20), ExpiresAt = now.AddMinutes(-10) },
                new IntegrationOAuthState { StateHash = "open-" + Guid.NewGuid(), UserId = w.Admin.Id, WorkspaceId = w.WorkspaceId, Verifier = "x", CreatedAt = now, ExpiresAt = now.AddMinutes(10) });
            await db.SaveChangesAsync();
        }

        await Reconciler().RunOnceAsync(CancellationToken.None);

        await using var after = api.Db();
        Assert.Equal(["fresh"], await after.IntegrationWebhookReceipts.Where(r => r.ConnectionId == w.ConnectionId).Select(r => r.DeliveryId).ToListAsync());
        Assert.Equal(["open-"], (await after.IntegrationOAuthStates.Where(s => s.UserId == w.Admin.Id).Select(s => s.StateHash).ToListAsync()).Select(h => h[..5]));
    }

    [Fact]
    public async Task Starting_an_oauth_round_trip_clears_the_states_that_expired()
    {
        var admin = await api.NewClient().SignedUpAsync("rec-state");
        var site = api.Jira.Site();
        await JiraKit.StartAsync(admin, site.Subdomain);
        await using (var db = api.Db())
        {
            var past = api.Clock.GetUtcNow().AddSeconds(-1);
            await db.IntegrationOAuthStates.Where(s => s.UserId == admin.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, past));
        }

        await JiraKit.StartAsync(admin, site.Subdomain);

        await using var after = api.Db();
        Assert.Equal(1, await after.IntegrationOAuthStates.CountAsync(s => s.UserId == admin.Id)); // only the new one is left
    }
}
