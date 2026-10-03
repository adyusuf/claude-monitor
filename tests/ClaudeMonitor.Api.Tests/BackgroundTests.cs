using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ClaudeMonitor.Api.Background;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

[Collection(ApiGroup.Name)]
public sealed class BackgroundTests(ApiFactory api)
{
    [Fact]
    public async Task The_housekeeper_expires_what_ran_out_of_time_and_creates_partitions()
    {
        var owner = await api.NewClient().SignedUpAsync("house");
        var agent = await owner.ConnectAgentAsync();
        var s = "sess-" + Guid.NewGuid();
        await agent.SendAsync(TestAgent.Hook(s, "SessionStart", new { }, api.Clock.GetUtcNow()));
        var id = (await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/sessions")).GetProperty("items")[0].GetProperty("id").GetGuid();
        var command = (await (await owner.PostAsync($"/api/sessions/{id}/commands", new { kind = "stop" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var permission = (await (await agent.Http.PostAsJsonAsync("/api/agent/permission-requests",
            new PermissionRequestCreate(HarnessKinds.ClaudeCode, s, "Bash", JsonSerializer.SerializeToElement(new { }), 10), TestUser.Json))
            .Content.ReadFromJsonAsync<PermissionRequestCreated>(TestUser.Json))!.Id;
        var device = await api.CreateClient().PostAsJsonAsync("/api/device/code",
            new DeviceCodeRequest("machine-" + Guid.NewGuid().ToString("N"), "h", "macos", "arm64", "0.3.0"), TestUser.Json);
        var deviceCode = (await device.Content.ReadFromJsonAsync<DeviceCodeResponse>(TestUser.Json))!.UserCode;

        api.Clock.Advance(TimeSpan.FromHours(1));
        await using var db = api.Db();
        await Housekeeper.RunOnceAsync(db, api.Clock.GetUtcNow(), CancellationToken.None);
        Assert.Equal("expired", (await db.SessionCommands.AsNoTracking().FirstAsync(c => c.Id == command)).Status);
        Assert.Equal("expired", (await db.PermissionRequests.AsNoTracking().FirstAsync(p => p.Id == permission)).Status);
        Assert.Equal("expired", (await db.DeviceAuthorizations.AsNoTracking().FirstAsync(d => d.UserCode == deviceCode)).Status);

        var later = api.Clock.GetUtcNow().AddMonths(Housekeeper.MonthsAhead);
        var name = $"session_events_y{later.Year:D4}m{later.Month:D2}";
        Assert.True(await db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\" FROM pg_class WHERE relname = {name}").AnyAsync());
        await Housekeeper.RunOnceAsync(db, api.Clock.GetUtcNow(), CancellationToken.None); // idempotent
    }

    [Fact]
    public async Task Events_past_retention_are_zipped_day_by_day_and_removed()
    {
        var owner = await api.NewClient().SignedUpAsync("archive");
        await owner.SendAsync(HttpMethod.Put, $"/api/workspaces/{owner.WorkspaceId}/settings", new { retentionDays = 1 });
        var agent = await owner.ConnectAgentAsync();
        var s = "sess-" + Guid.NewGuid();
        await agent.SendAsync(TestAgent.Hook(s, "SessionStart", new { secret = "kept in the archive" }, api.Clock.GetUtcNow()),
            TestAgent.Hook(s, "Stop", new { }, api.Clock.GetUtcNow()));
        api.Clock.Advance(TimeSpan.FromDays(1));
        var nextDay = await owner.ConnectAgentAsync(); // the first agent's access token expired overnight
        await nextDay.SendAsync(TestAgent.Hook(s, "UserPromptSubmit", new { prompt = "next day" }, api.Clock.GetUtcNow()));
        api.Clock.Advance(TimeSpan.FromDays(1));

        await using var db = api.Db();
        var written = await Archiver.RunOnceAsync(db, api.ArchiveDir, api.Clock.GetUtcNow(), CancellationToken.None);
        Assert.True(written >= 1);
        var archives = await db.EventArchives.AsNoTracking().Where(a => a.WorkspaceId == owner.WorkspaceId).OrderBy(a => a.Day).ToListAsync();
        Assert.Single(archives);
        var archive = archives[0];
        Assert.Equal(2, archive.EventCount);
        await using (var file = File.OpenRead(archive.Path))
        {
            Assert.Equal(archive.Sha256, Convert.ToHexStringLower(await SHA256.HashDataAsync(file)));
        }

        using (var zip = ZipFile.OpenRead(archive.Path))
        {
            using var reader = new StreamReader(zip.Entries.Single().Open());
            var events = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement;
            Assert.Equal(2, events.GetArrayLength());
            Assert.Equal("kept in the archive", events[0].GetProperty("payload").GetProperty("secret").GetString());
        }

        var left = await db.SessionEvents.AsNoTracking().CountAsync(e => e.WorkspaceId == owner.WorkspaceId);
        Assert.Equal(1, left);
        await Archiver.RunOnceAsync(db, api.ArchiveDir, api.Clock.GetUtcNow(), CancellationToken.None);
        Assert.Equal(1, await db.EventArchives.CountAsync(a => a.WorkspaceId == owner.WorkspaceId));
    }
}
