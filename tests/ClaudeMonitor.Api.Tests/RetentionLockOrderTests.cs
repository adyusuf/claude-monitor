using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>
/// Retention deletes a run's output after locking the run's row, the order account deletion takes them in (runs, then
/// output), so the two over the same old run never wait on each other (40P01).
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class RetentionLockOrderTests(ApiFactory api)
{
    private async Task<int> WaitingBackendsAsync()
    {
        await using var db = api.Db();
        return await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE wait_event_type = 'Lock'").SingleAsync();
    }

    /// <summary>
    /// The test stands in for an account deletion that holds the old run's row. Retention stops at that row; the deletion then
    /// takes the run's output rows, which only works if retention has not already deleted them (it would hold them while
    /// waiting for the run: a deadlock). Then the deletion ends and retention finishes.
    /// </summary>
    [Fact]
    public async Task Retention_waits_at_a_locked_old_run_without_holding_its_output()
    {
        var team = await RemoteKit.TeamAsync(api);
        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/workspaces/{team.WorkspaceId}/settings", new { retentionDays = 1 })).EnsureSuccessStatusCode();
        var run = (await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()))).Id;
        (await team.Admin.PostAsync($"/api/runs/{run}/cancel")).EnsureSuccessStatusCode();
        await using (var db = api.Db())
        {
            db.RemoteRunOutput.Add(new RemoteRunOutput { RunId = run, Seq = 0, Stream = "stdout", Body = "old", Bytes = 3, ReceivedAt = api.Clock.GetUtcNow() });
            await db.SaveChangesAsync();
        }

        api.Clock.Advance(TimeSpan.FromDays(2));
        await using var holder = api.Db();
        await using var hold = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlAsync($"SELECT 1 FROM remote_runs WHERE id = {run} FOR UPDATE");
        var retention = Task.Run(() => RemoteKit.HousekeepAsync(api));
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (await WaitingBackendsAsync() < 1) await Task.Delay(20, timeout.Token);
        }

        await holder.Database.ExecuteSqlAsync($"SELECT 1 FROM remote_run_output WHERE run_id = {run} FOR UPDATE");
        await hold.CommitAsync();
        await retention;

        await using var check = api.Db();
        Assert.False(await check.RemoteRuns.AnyAsync(r => r.Id == run));
        Assert.False(await check.RemoteRunOutput.AnyAsync(o => o.RunId == run));
    }
}
