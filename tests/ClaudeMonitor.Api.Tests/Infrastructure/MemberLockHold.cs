using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests.Infrastructure;

/// <summary>
/// Starts requests together after each has read what it reads before taking locks: the member rows in <c>held</c> are locked
/// by the test meanwhile, so every request blocks on them, and they are released once all requests are waiting.
/// </summary>
public static class MemberLockHold
{
    public static async Task<HttpResponseMessage[]> RunAsync(
        ApiFactory api, Guid workspaceId, Guid[] held, params Func<Task<HttpResponseMessage>>[] actions)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(actions);
        await using var holder = api.Db();
        await using var hold = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM workspace_members WHERE workspace_id = {workspaceId} AND user_id = ANY({held}) FOR UPDATE");
        var running = actions.Select(a => Task.Run(a)).ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await WaitingBackendsAsync(api) < actions.Length) await Task.Delay(20, timeout.Token);
        await hold.CommitAsync();
        return await Task.WhenAll(running);
    }

    private static async Task<int> WaitingBackendsAsync(ApiFactory api)
    {
        await using var db = api.Db();
        return await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE wait_event_type = 'Lock'").SingleAsync();
    }
}
