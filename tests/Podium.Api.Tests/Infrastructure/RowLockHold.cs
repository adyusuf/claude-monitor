using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>
/// Starts requests together after each has read what it reads before it writes: the rows the lock query selects are locked FOR UPDATE by the
/// test meanwhile, so every request blocks on its first write to them, and they are released once all requests are waiting (as
/// <see cref="MemberLockHold"/>, for any rows).
/// </summary>
public static class RowLockHold
{
    public static async Task<HttpResponseMessage[]> RunAsync(ApiFactory api, FormattableString lockQuery, params Func<Task<HttpResponseMessage>>[] actions)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(actions);
        await using var holder = api.Db();
        await using var hold = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlAsync(lockQuery);
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
