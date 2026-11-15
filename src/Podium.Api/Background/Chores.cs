using Podium.Api.Data;

namespace Podium.Api.Background;

/// <summary>
/// Runs independent periodic chores one after another. One that fails does not stop the ones after it (retention runs last and
/// must not wait on an earlier chore that keeps failing); the failures are thrown together once every chore has had its turn.
/// </summary>
public static class Chores
{
    public static async Task RunAsync(PodiumDb db, CancellationToken ct, params Func<Task>[] chores)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(chores);
        List<Exception>? failed = null;
        foreach (var chore in chores)
        {
            try
            {
                await chore();
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                (failed ??= []).Add(e);
                // What the failed chore left tracked must not be saved, or fail again, with the next one.
                db.ChangeTracker.Clear();
            }
        }

        if (failed is not null) throw new AggregateException("periodic chores failed", failed);
    }
}
