using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// F-049: a settings save from a page that was loaded before someone else saved can carry <c>expectedUpdatedAt</c>: the value the page read. A
/// stale one is 409 stale_update and changes nothing; one without it saves as it always did (old clients). The workspace settings and the remote
/// settings are one row, so a save of either makes the other form's page stale.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class WorkspaceSettingsStaleTests(ApiFactory api)
{
    // A person signed up through the test client has their own workspace as WorkspaceId: the second admin works in the first one's.
    private static string SettingsUrl(Guid ws) => $"/api/workspaces/{ws}/settings";

    private static string RemoteUrl(Guid ws) => $"/api/workspaces/{ws}/remote-settings";

    private static async Task<string> StampAsync(TestUser user, Guid ws) =>
        (await user.GetJsonAsync($"/api/workspaces/{ws}")).GetProperty("settings").GetProperty("updatedAt").GetString()!;

    private static async Task<string> RemoteStampAsync(TestUser user, Guid ws) => (await user.GetJsonAsync(RemoteUrl(ws))).GetProperty("updatedAt").GetString()!;

    private static async Task AssertStaleAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("stale_update", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    /// <summary>The test clock stands still between calls, so each save is dated a second after the one before it, as real ones are.</summary>
    private void NextSecond() => api.Clock.Advance(TimeSpan.FromSeconds(1));

    private async Task<(TestUser First, TestUser Second)> TwoAdminsAsync(string tag)
    {
        var first = await api.NewClient().SignedUpAsync(tag + "-a");
        var second = await RemoteKit.MemberAsync(api, first, tag + "-b", "admin");
        return (first, second);
    }

    [Fact]
    public async Task The_second_save_from_a_page_loaded_before_the_first_is_409_and_changes_nothing()
    {
        var (first, second) = await TwoAdminsAsync("stale-ws");
        var firstStamp = await StampAsync(first, first.WorkspaceId);
        var secondStamp = await StampAsync(second, first.WorkspaceId);
        Assert.Equal(firstStamp, secondStamp);
        NextSecond();
        (await second.SendAsync(HttpMethod.Put, SettingsUrl(first.WorkspaceId), new { eventMaxBytes = 4096, expectedUpdatedAt = secondStamp })).EnsureSuccessStatusCode();
        var rowsBefore = await AuditRowsAsync(first);

        await AssertStaleAsync(await first.SendAsync(HttpMethod.Put, SettingsUrl(first.WorkspaceId), new { eventMaxBytes = 8192, agentUpdate = "check", expectedUpdatedAt = firstStamp }));

        var settings = (await first.GetJsonAsync($"/api/workspaces/{first.WorkspaceId}")).GetProperty("settings");
        Assert.Equal((4096, "off"), (settings.GetProperty("eventMaxBytes").GetInt32(), settings.GetProperty("agentUpdate").GetString()));
        Assert.Equal(rowsBefore, await AuditRowsAsync(first)); // the refused save writes no audit row
    }

    [Fact]
    public async Task A_save_with_the_value_just_read_goes_through_and_hands_out_a_new_one()
    {
        var owner = await api.NewClient().SignedUpAsync("stale-fresh");
        var stamp = await StampAsync(owner, owner.WorkspaceId);
        NextSecond();

        (await owner.SendAsync(HttpMethod.Put, SettingsUrl(owner.WorkspaceId), new { eventMaxBytes = 4096, expectedUpdatedAt = stamp })).EnsureSuccessStatusCode();

        var next = await StampAsync(owner, owner.WorkspaceId);
        Assert.NotEqual(stamp, next);
        NextSecond();
        (await owner.SendAsync(HttpMethod.Put, SettingsUrl(owner.WorkspaceId), new { eventMaxBytes = 2048, expectedUpdatedAt = next })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_save_without_the_value_is_as_before_even_when_someone_else_saved_in_between()
    {
        var (first, second) = await TwoAdminsAsync("stale-old");
        (await second.SendAsync(HttpMethod.Put, SettingsUrl(first.WorkspaceId), new { eventMaxBytes = 4096 })).EnsureSuccessStatusCode();

        var response = await first.SendAsync(HttpMethod.Put, SettingsUrl(first.WorkspaceId), new { eventMaxBytes = 8192 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(8192, (await first.GetJsonAsync($"/api/workspaces/{first.WorkspaceId}")).GetProperty("settings").GetProperty("eventMaxBytes").GetInt32());
    }

    [Fact]
    public async Task Two_saves_of_the_same_page_at_once_let_exactly_one_through()
    {
        var (first, second) = await TwoAdminsAsync("stale-race");
        var stamp = await StampAsync(first, first.WorkspaceId);
        NextSecond();

        var answers = await Task.WhenAll(
            first.SendAsync(HttpMethod.Put, SettingsUrl(first.WorkspaceId), new { eventMaxBytes = 4096, expectedUpdatedAt = stamp }),
            second.SendAsync(HttpMethod.Put, SettingsUrl(first.WorkspaceId), new { eventMaxBytes = 8192, expectedUpdatedAt = stamp }));

        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Conflict], answers.Select(a => a.StatusCode).Order());
    }

    [Fact]
    public async Task The_remote_switch_another_admin_turned_off_is_not_turned_back_on_by_a_stale_card()
    {
        var (first, second) = await TwoAdminsAsync("stale-remote");
        (await first.SendAsync(HttpMethod.Put, RemoteUrl(first.WorkspaceId), new { remoteRunsEnabled = true })).EnsureSuccessStatusCode();
        var firstStamp = await RemoteStampAsync(first, first.WorkspaceId); // the first admin's card was loaded with the switch on
        var secondStamp = await RemoteStampAsync(second, first.WorkspaceId);
        NextSecond();
        (await second.SendAsync(HttpMethod.Put, RemoteUrl(first.WorkspaceId), new { remoteRunsEnabled = false, expectedUpdatedAt = secondStamp })).EnsureSuccessStatusCode();

        await AssertStaleAsync(await first.SendAsync(HttpMethod.Put, RemoteUrl(first.WorkspaceId),
            new { remoteRunsEnabled = true, alertCpuPct = 80, alertMemoryPct = 90, alertDiskPct = 90, alertSustainSeconds = 300, expectedUpdatedAt = firstStamp }));

        Assert.False((await first.GetJsonAsync(RemoteUrl(first.WorkspaceId))).GetProperty("remoteRunsEnabled").GetBoolean());
    }

    [Fact]
    public async Task A_remote_save_without_the_value_is_as_before_and_one_with_the_value_just_read_goes_through()
    {
        var (first, second) = await TwoAdminsAsync("stale-remote-old");
        (await second.SendAsync(HttpMethod.Put, RemoteUrl(first.WorkspaceId), new { alertCpuPct = 70 })).EnsureSuccessStatusCode();
        NextSecond();
        Assert.Equal(HttpStatusCode.OK, (await first.SendAsync(HttpMethod.Put, RemoteUrl(first.WorkspaceId), new { alertCpuPct = 60 })).StatusCode);
        var stamp = await RemoteStampAsync(first, first.WorkspaceId);
        NextSecond();

        var saved = await first.SendAsync(HttpMethod.Put, RemoteUrl(first.WorkspaceId), new { alertCpuPct = 50, expectedUpdatedAt = stamp });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.NotEqual(stamp, (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("updatedAt").GetString());
    }

    [Fact]
    public async Task A_remote_save_makes_the_workspace_settings_page_stale_because_it_is_one_row()
    {
        var owner = await api.NewClient().SignedUpAsync("stale-cross");
        var stamp = await StampAsync(owner, owner.WorkspaceId);
        NextSecond();
        (await owner.SendAsync(HttpMethod.Put, RemoteUrl(owner.WorkspaceId), new { alertCpuPct = 70 })).EnsureSuccessStatusCode();

        await AssertStaleAsync(await owner.SendAsync(HttpMethod.Put, SettingsUrl(owner.WorkspaceId), new { eventMaxBytes = 4096, expectedUpdatedAt = stamp }));
    }

    private async Task<int> AuditRowsAsync(TestUser user)
    {
        await using var db = api.Db();
        return await db.AuditEvents.CountAsync(a => a.WorkspaceId == user.WorkspaceId && a.Action == AuditActions.SettingsChanged);
    }
}
