using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>The audit log is newest first, pages with the <c>before</c> cursor and clamps <c>limit</c> to 1..100 with 25 as the default (MachineEndpoints.AuditLog, Http.Limit).</summary>
[Collection(ApiGroup.Name)]
public sealed class WorkspaceAuditPagingTests(ApiFactory api)
{
    private static string AuditUrl(TestUser user, string query) => $"/api/workspaces/{user.WorkspaceId}/audit{query}";

    private static List<long> IdsOf(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("id").GetInt64()).ToList();

    private static string? NextOf(JsonElement page) => page.GetProperty("next").GetString();

    private static async Task ChangeSettingsAsync(TestUser user, int times)
    {
        for (var i = 1; i <= times; i++)
        {
            (await user.SendAsync(HttpMethod.Put, $"/api/workspaces/{user.WorkspaceId}/settings", new { retentionDays = 10 + i })).EnsureSuccessStatusCode();
        }
    }

    private async Task SeedRowsAsync(TestUser user, int count)
    {
        await using var db = api.Db();
        db.AuditEvents.AddRange(Enumerable.Range(0, count).Select(_ => new AuditEvent
        {
            WorkspaceId = user.WorkspaceId,
            ActorUserId = user.Id,
            Action = AuditActions.SettingsChanged,
            At = api.Clock.GetUtcNow(),
        }));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Walking_the_pages_two_rows_at_a_time_returns_every_row_once_newest_first()
    {
        var owner = await api.NewClient().SignedUpAsync("pg-walk");
        await ChangeSettingsAsync(owner, 5);
        var everything = IdsOf(await owner.GetJsonAsync(AuditUrl(owner, "?limit=100")));

        var walked = new List<long>();
        var first = await owner.GetJsonAsync(AuditUrl(owner, "?limit=2"));
        var page = first;
        while (true)
        {
            walked.AddRange(IdsOf(page));
            if (NextOf(page) is not { } next) break;
            page = await owner.GetJsonAsync(AuditUrl(owner, "?limit=2&before=" + next));
        }

        Assert.Equal(everything, walked);
        Assert.Equal(everything.OrderByDescending(id => id), walked);
        Assert.Equal(2, IdsOf(first).Count);
    }

    [Fact]
    public async Task The_before_cursor_returns_only_older_rows()
    {
        var owner = await api.NewClient().SignedUpAsync("pg-before");
        await ChangeSettingsAsync(owner, 4);
        var everything = IdsOf(await owner.GetJsonAsync(AuditUrl(owner, "?limit=100")));
        var cursor = everything[1];

        var older = IdsOf(await owner.GetJsonAsync(AuditUrl(owner, $"?limit=100&before={cursor}")));

        Assert.Equal(everything.Count - 2, older.Count);
        Assert.All(older, id => Assert.True(id < cursor));
    }

    [Fact]
    public async Task A_page_that_does_not_reach_the_end_carries_the_cursor_of_its_last_row_and_the_last_page_carries_none()
    {
        var owner = await api.NewClient().SignedUpAsync("pg-next");
        await ChangeSettingsAsync(owner, 2);
        var everything = IdsOf(await owner.GetJsonAsync(AuditUrl(owner, "?limit=100")));

        var cut = await owner.GetJsonAsync(AuditUrl(owner, $"?limit={everything.Count - 1}"));
        var whole = await owner.GetJsonAsync(AuditUrl(owner, $"?limit={everything.Count}"));

        Assert.Equal(IdsOf(cut)[^1].ToString(System.Globalization.CultureInfo.InvariantCulture), NextOf(cut));
        Assert.Null(NextOf(whole));
    }

    [Theory]
    [InlineData("", 25)]
    [InlineData("?limit=7", 7)]
    [InlineData("?limit=1000", 100)]
    [InlineData("?limit=0", 1)]
    [InlineData("?limit=-5", 1)]
    public async Task The_page_size_is_clamped_between_1_and_100_with_25_by_default(string query, int expected)
    {
        var owner = await api.NewClient().SignedUpAsync("pg-limit");
        await SeedRowsAsync(owner, 120);

        var items = IdsOf(await owner.GetJsonAsync(AuditUrl(owner, query)));

        Assert.Equal(expected, items.Count);
    }
}
