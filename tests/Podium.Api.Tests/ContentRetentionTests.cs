using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Background;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Podium.Api.Tests;

/// <summary>
/// F-119: past the workspace's retention a web prompt's text and a permission request's tool input are blanked (the rows stay), a pending
/// command or an open request never; the archive files get the same lifetime once more. Another workspace's retention is its own. The readers
/// see "removed" at once.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class ContentRetentionTests(ApiFactory api)
{
    private const string Secret = "prompt text past retention";

    /// <summary>A session team whose workspace keeps content for <paramref name="days"/> days.</summary>
    private async Task<SessionTeam> TeamAsync(int days)
    {
        var team = await SessionKit.TeamAsync(api);
        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/workspaces/{team.WorkspaceId}/settings", new { retentionDays = days })).EnsureSuccessStatusCode();
        return team;
    }

    private static async Task<Guid> PromptAsync(SessionTeam team, string body = Secret)
    {
        var response = await team.Owner.PostAsync($"/api/sessions/{team.SessionId}/commands", new { kind = "prompt", body });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Moves a command back in time, with a status.</summary>
    private async Task AgeCommandAsync(Guid id, TimeSpan age, string status)
    {
        await using var db = api.Db();
        var at = api.Clock.GetUtcNow() - age;
        await db.SessionCommands.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.CreatedAt, at).SetProperty(c => c.Status, status));
    }

    private async Task AgePermissionAsync(Guid id, TimeSpan age, string status)
    {
        await using var db = api.Db();
        var at = api.Clock.GetUtcNow() - age;
        await db.PermissionRequests.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedAt, at).SetProperty(p => p.Status, status));
    }

    private async Task<int> PassAsync(int batch = ContentRetention.Batch)
    {
        await using var db = api.Db();
        return await ContentRetention.RunOnceAsync(db, api.ArchiveDir, NullLogger.Instance, api.Clock.GetUtcNow(), CancellationToken.None, batch);
    }

    private async Task<string?> BodyAsync(Guid id)
    {
        await using var db = api.Db();
        return await db.SessionCommands.Where(c => c.Id == id).Select(c => c.Body).SingleAsync();
    }

    private async Task<string> InputAsync(Guid id)
    {
        await using var db = api.Db();
        var row = await db.PermissionRequests.AsNoTracking().SingleAsync(p => p.Id == id);
        return row.ToolInput.RootElement.GetRawText();
    }

    [Fact]
    public async Task A_prompt_past_retention_is_blanked_a_younger_or_pending_one_is_not_and_the_row_stays()
    {
        var team = await TeamAsync(1);
        var old = await PromptAsync(team);
        var young = await PromptAsync(team, "young prompt");
        var pending = await PromptAsync(team, "still queued");
        await AgeCommandAsync(old, TimeSpan.FromDays(2), CommandStatuses.Applied);
        await AgeCommandAsync(young, TimeSpan.FromHours(12), CommandStatuses.Applied);
        await AgeCommandAsync(pending, TimeSpan.FromDays(2), CommandStatuses.Queued);

        await PassAsync();

        Assert.Null(await BodyAsync(old));
        Assert.Equal("young prompt", await BodyAsync(young));
        Assert.Equal("still queued", await BodyAsync(pending));
        await using var db = api.Db();
        var kept = await db.SessionCommands.AsNoTracking().SingleAsync(c => c.Id == old);
        Assert.Equal((CommandStatuses.Applied, team.Owner.Id), (kept.Status, kept.CreatedBy));
    }

    [Fact]
    public async Task A_permission_input_past_retention_is_blanked_and_an_open_request_never_is()
    {
        var team = await TeamAsync(1);
        var answered = await SessionKit.AskPermissionAsync(team);
        var open = await SessionKit.AskPermissionAsync(team);
        var young = await SessionKit.AskPermissionAsync(team);
        await AgePermissionAsync(answered, TimeSpan.FromDays(2), PermissionStatuses.Answered);
        await AgePermissionAsync(open, TimeSpan.FromDays(2), PermissionStatuses.Open);
        await AgePermissionAsync(young, TimeSpan.FromHours(12), PermissionStatuses.Expired);

        await PassAsync();

        Assert.Equal("{}", await InputAsync(answered));
        Assert.Contains("git push", await InputAsync(open), StringComparison.Ordinal);
        Assert.Contains("git push", await InputAsync(young), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_chosen_answers_of_an_old_request_are_cleared_with_its_input_and_a_young_ones_are_kept()
    {
        var team = await TeamAsync(1);
        var old = await SessionKit.AskPermissionAsync(team);
        var young = await SessionKit.AskPermissionAsync(team);
        await using (var db = api.Db())
        {
            await db.PermissionRequests.Where(p => p.Id == old || p.Id == young)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Answers, "{\"which\":\"typed answer\"}"));
        }

        await AgePermissionAsync(old, TimeSpan.FromDays(2), PermissionStatuses.Answered);
        await AgePermissionAsync(young, TimeSpan.FromHours(12), PermissionStatuses.Answered);

        await PassAsync();

        await using var read = api.Db();
        Assert.Null(await read.PermissionRequests.Where(p => p.Id == old).Select(p => p.Answers).SingleAsync());
        Assert.Contains("typed answer", await read.PermissionRequests.Where(p => p.Id == young).Select(p => p.Answers).SingleAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_workspace_keeps_its_content_for_its_own_retention()
    {
        var team = await TeamAsync(1);
        var other = await SessionKit.TeamAsync(api); // the default retention (90 days)
        var mine = await PromptAsync(team);
        var theirs = await PromptAsync(other, "theirs");
        var theirAsk = await SessionKit.AskPermissionAsync(other);
        await AgeCommandAsync(mine, TimeSpan.FromDays(2), CommandStatuses.Applied);
        await AgeCommandAsync(theirs, TimeSpan.FromDays(2), CommandStatuses.Applied);
        await AgePermissionAsync(theirAsk, TimeSpan.FromDays(2), PermissionStatuses.Answered);

        await PassAsync();

        Assert.Null(await BodyAsync(mine));
        Assert.Equal("theirs", await BodyAsync(theirs));
        Assert.Contains("git push", await InputAsync(theirAsk), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pass_takes_one_batch_and_a_second_pass_changes_nothing_more()
    {
        var team = await TeamAsync(1);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var id = await PromptAsync(team, $"prompt {i}");
            await AgeCommandAsync(id, TimeSpan.FromDays(2), CommandStatuses.Applied);
            ids.Add(id);
        }

        async Task<int> LeftAsync()
        {
            await using var db = api.Db();
            return await db.SessionCommands.CountAsync(c => ids.Contains(c.Id) && c.Body != null);
        }

        await PassAsync(batch: 2);
        Assert.Equal(1, await LeftAsync());
        await PassAsync(batch: 2);
        Assert.Equal(0, await LeftAsync());
        await PassAsync(batch: 2);
        Assert.Equal(0, await LeftAsync());
    }

    [Fact]
    public async Task An_archive_file_older_than_the_retention_is_deleted_with_its_row_and_a_younger_one_stays()
    {
        var team = await TeamAsync(1);
        var dir = Path.Combine(api.ArchiveDir, team.WorkspaceId.ToString());
        Directory.CreateDirectory(dir);
        var oldPath = Path.Combine(dir, "old.json.zip");
        var youngPath = Path.Combine(dir, "young.json.zip");
        await File.WriteAllTextAsync(oldPath, "old");
        await File.WriteAllTextAsync(youngPath, "young");
        var now = api.Clock.GetUtcNow();
        var old = new EventArchive { WorkspaceId = team.WorkspaceId, Day = DateOnly.FromDateTime(now.AddDays(-5).UtcDateTime), Path = oldPath, CreatedAt = now.AddDays(-2) };
        var young = new EventArchive { WorkspaceId = team.WorkspaceId, Day = DateOnly.FromDateTime(now.AddDays(-2).UtcDateTime), Path = youngPath, CreatedAt = now.AddHours(-12) };
        var gone = new EventArchive { WorkspaceId = team.WorkspaceId, Day = DateOnly.FromDateTime(now.AddDays(-6).UtcDateTime), Path = Path.Combine(dir, "missing.json.zip"), CreatedAt = now.AddDays(-3) };
        await using (var db = api.Db())
        {
            db.EventArchives.AddRange(old, young, gone);
            await db.SaveChangesAsync();
        }

        await PassAsync();

        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(youngPath));
        await using var check = api.Db();
        Assert.Equal([young.Id], await check.EventArchives.Where(a => a.WorkspaceId == team.WorkspaceId).Select(a => a.Id).ToListAsync());
    }

    [Fact]
    public async Task The_session_page_shows_a_prompt_and_an_input_past_retention_as_removed_at_once()
    {
        var team = await TeamAsync(1);
        var old = await PromptAsync(team);
        var young = await PromptAsync(team, "young prompt");
        var ask = await SessionKit.AskPermissionAsync(team);
        var openAsk = await SessionKit.AskPermissionAsync(team);
        await AgeCommandAsync(old, TimeSpan.FromDays(2), CommandStatuses.Applied);
        await AgeCommandAsync(young, TimeSpan.FromHours(1), CommandStatuses.Applied);
        await AgePermissionAsync(ask, TimeSpan.FromDays(2), PermissionStatuses.Answered);

        // Before any housekeeping pass: the reader already sees them removed.
        var commands = (await team.Admin.GetJsonAsync($"/api/sessions/{team.SessionId}/commands")).EnumerateArray().ToDictionary(c => c.GetProperty("id").GetGuid());
        Assert.Equal((JsonValueKind.Null, true), (commands[old].GetProperty("body").ValueKind, commands[old].GetProperty("contentRemoved").GetBoolean()));
        Assert.Equal(("young prompt", false), (commands[young].GetProperty("body").GetString(), commands[young].GetProperty("contentRemoved").GetBoolean()));
        var asks = (await team.Admin.GetJsonAsync($"/api/sessions/{team.SessionId}/permission-requests")).EnumerateArray().ToDictionary(p => p.GetProperty("id").GetGuid());
        Assert.Equal(("{}", true), (asks[ask].GetProperty("toolInput").GetRawText(), asks[ask].GetProperty("contentRemoved").GetBoolean()));
        Assert.False(asks[openAsk].GetProperty("contentRemoved").GetBoolean());
        var answered = (await team.Owner.GetJsonAsync($"/api/workspaces/{team.WorkspaceId}/permission-requests?status=answered")).EnumerateArray()
            .Single(p => p.GetProperty("id").GetGuid() == ask);
        Assert.Equal(("{}", true), (answered.GetProperty("toolInput").GetRawText(), answered.GetProperty("contentRemoved").GetBoolean()));
    }
}
