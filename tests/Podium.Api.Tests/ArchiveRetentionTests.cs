using Podium.Api.Background;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Podium.Api.Tests;

/// <summary>
/// I-2: the archive files past retention are deleted only under the archive folder; a row that points anywhere else loses its row and keeps its
/// file, and one file that cannot be deleted neither stops the others nor makes the pass throw. The log names the archive, never its path.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class ArchiveRetentionTests(ApiFactory api)
{
    private readonly ListLogger<Housekeeper> log = new();

    private async Task<Guid> WorkspaceAsync()
    {
        var team = await SessionKit.TeamAsync(api);
        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/workspaces/{team.WorkspaceId}/settings", new { retentionDays = 1 })).EnsureSuccessStatusCode();
        return team.WorkspaceId;
    }

    /// <summary>An archive row past retention; <paramref name="ageDays"/> orders them (the oldest first).</summary>
    private async Task<EventArchive> RowAsync(Guid workspaceId, string path, int ageDays)
    {
        var now = api.Clock.GetUtcNow();
        var row = new EventArchive { WorkspaceId = workspaceId, Day = DateOnly.FromDateTime(now.AddDays(-ageDays - 3).UtcDateTime), Path = path, CreatedAt = now.AddDays(-ageDays) };
        await using var db = api.Db();
        db.EventArchives.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    private async Task PassAsync()
    {
        await using var db = api.Db();
        await ContentRetention.RunOnceAsync(db, api.ArchiveDir, log, api.Clock.GetUtcNow(), CancellationToken.None);
    }

    private async Task<List<Guid>> RowsOfAsync(Guid workspaceId)
    {
        await using var db = api.Db();
        return await db.EventArchives.Where(a => a.WorkspaceId == workspaceId).Select(a => a.Id).ToListAsync();
    }

    [Fact]
    public async Task A_row_whose_path_is_outside_the_archive_folder_leaves_the_file_alone_and_loses_its_row()
    {
        var workspaceId = await WorkspaceAsync();
        var outside = Path.Combine(Path.GetTempPath(), "ap-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var victim = Path.Combine(outside, "keep.txt");
            await File.WriteAllTextAsync(victim, "not an archive");
            // A sibling whose name starts like the archive folder is outside it too, and so is a path that climbs out of it.
            var sibling = api.ArchiveDir + "-sibling";
            Directory.CreateDirectory(sibling);
            var lookalike = Path.Combine(sibling, "keep.txt");
            await File.WriteAllTextAsync(lookalike, "not an archive");
            var climbing = Path.Combine(api.ArchiveDir, "..", Path.GetFileName(outside), "keep.txt");
            var rows = new[] { await RowAsync(workspaceId, victim, 4), await RowAsync(workspaceId, lookalike, 3), await RowAsync(workspaceId, climbing, 2) };

            await PassAsync();

            Assert.True(File.Exists(victim));
            Assert.True(File.Exists(lookalike));
            Assert.Empty(await RowsOfAsync(workspaceId)); // not tried again every pass, never in the way of the rows after it
            var warnings = log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Text).ToList();
            Assert.All(rows, r => Assert.Contains(warnings, w => w.Contains(r.Id.ToString(), StringComparison.Ordinal)));
            Assert.DoesNotContain(warnings, w => w.Contains(outside, StringComparison.Ordinal) || w.Contains(sibling, StringComparison.Ordinal));
            Directory.Delete(sibling, recursive: true);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task A_file_that_cannot_be_deleted_keeps_its_row_and_the_files_after_it_are_still_deleted()
    {
        var workspaceId = await WorkspaceAsync();
        var dir = Path.Combine(api.ArchiveDir, workspaceId.ToString());
        Directory.CreateDirectory(dir);
        var stuck = Path.Combine(dir, "stuck.json.zip");
        Directory.CreateDirectory(stuck); // a directory where the file should be: File.Delete fails on it
        var later = Path.Combine(dir, "later.json.zip");
        await File.WriteAllTextAsync(later, "later");
        var first = await RowAsync(workspaceId, stuck, 3);
        await RowAsync(workspaceId, later, 2);

        var error = await Record.ExceptionAsync(PassAsync);

        Assert.Null(error);
        Assert.False(File.Exists(later));
        Assert.Equal([first.Id], await RowsOfAsync(workspaceId)); // tried again next pass
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains(first.Id.ToString(), StringComparison.Ordinal)).Text;
        Assert.Contains(nameof(UnauthorizedAccessException), warning, StringComparison.Ordinal); // the kind of error, not its message (which holds the path)
        Assert.DoesNotContain(log.Entries, e => e.Text.Contains(dir, StringComparison.Ordinal));

        // Leave nothing that warns in the passes of other tests.
        Directory.Delete(stuck);
        await using var db = api.Db();
        await db.EventArchives.Where(a => a.Id == first.Id).ExecuteDeleteAsync();
    }
}
