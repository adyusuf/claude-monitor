using Podium.Api.Background;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>A workspace whose day file cannot be written does not keep the workspaces after it from being archived.</summary>
[Collection(ApiGroup.Name)]
public sealed class ArchiverIsolationTests(ApiFactory api)
{
    /// <summary>A workspace with one day of retention and one session's events sent now.</summary>
    private async Task<Guid> WorkspaceWithEventsAsync(string tag)
    {
        var owner = await api.NewClient().SignedUpAsync(tag);
        (await owner.SendAsync(HttpMethod.Put, $"/api/workspaces/{owner.WorkspaceId}/settings", new { retentionDays = 1 })).EnsureSuccessStatusCode();
        var agent = await owner.ConnectAgentAsync();
        var s = "sess-" + Guid.NewGuid();
        await agent.SendAsync(TestAgent.Hook(s, "SessionStart", new { }, api.Clock.GetUtcNow()), TestAgent.Hook(s, "Stop", new { }, api.Clock.GetUtcNow()));
        return owner.WorkspaceId;
    }

    [Fact]
    public async Task One_workspaces_archive_failure_does_not_block_the_others()
    {
        var blocked = await WorkspaceWithEventsAsync("arch-blocked");
        var other = await WorkspaceWithEventsAsync("arch-other");
        // A file where the workspace's archive folder should be: its day file cannot be written.
        Directory.CreateDirectory(api.ArchiveDir);
        var block = Path.Combine(api.ArchiveDir, blocked.ToString());
        await File.WriteAllTextAsync(block, "not a folder");
        api.Clock.Advance(TimeSpan.FromDays(2));

        await using var db = api.Db();
        Exception? error;
        try
        {
            error = await Record.ExceptionAsync(() => Archiver.RunOnceAsync(db, api.ArchiveDir, api.Clock.GetUtcNow(), CancellationToken.None));
        }
        finally
        {
            File.Delete(block); // later archiver passes of other tests may write this workspace again
        }

        Assert.Equal(1, await db.EventArchives.CountAsync(a => a.WorkspaceId == other));
        Assert.Equal(0, await db.SessionEvents.CountAsync(e => e.WorkspaceId == other));
        Assert.Equal(0, await db.EventArchives.CountAsync(a => a.WorkspaceId == blocked));
        Assert.Equal(2, await db.SessionEvents.CountAsync(e => e.WorkspaceId == blocked));
        Assert.IsAssignableFrom<IOException>(Assert.Single(Assert.IsType<AggregateException>(error).Flatten().InnerExceptions));
    }
}
