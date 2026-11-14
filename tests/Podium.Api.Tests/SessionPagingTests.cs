using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>Paging the sessions list by key: sessions with the very same last-event time are neither repeated nor lost across a page boundary.</summary>
[Collection(ApiGroup.Name)]
public sealed class SessionPagingTests(ApiFactory api)
{
    private DateTimeOffset T(int seconds) => api.Clock.GetUtcNow().AddSeconds(seconds);

    private static async Task<(List<string?> Titles, string? Next)> PageAsync(TestUser user, string? cursor)
    {
        var page = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/sessions?limit=2" + (cursor is null ? "" : $"&cursor={cursor}"));
        var next = page.GetProperty("next");
        return (page.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("title").GetString()).ToList(),
            next.ValueKind == System.Text.Json.JsonValueKind.Null ? null : next.GetString());
    }

    [Fact]
    public async Task Sessions_with_the_same_last_event_time_are_each_listed_once_across_a_page_boundary()
    {
        var user = await api.NewClient().SignedUpAsync("tie-paging");
        var agent = await user.ConnectAgentAsync();
        await agent.SendAsync(
            TestAgent.Hook("s-older", "UserPromptSubmit", new { prompt = "older" }, T(-10)),
            TestAgent.Hook("s-tie-a", "UserPromptSubmit", new { prompt = "tie-a" }, T(0)),
            TestAgent.Hook("s-tie-b", "UserPromptSubmit", new { prompt = "tie-b" }, T(0)),
            TestAgent.Hook("s-tie-c", "UserPromptSubmit", new { prompt = "tie-c" }, T(0)),
            TestAgent.Hook("s-newer", "UserPromptSubmit", new { prompt = "newer" }, T(10)));

        var first = await PageAsync(user, null);
        var second = await PageAsync(user, first.Next);
        var third = await PageAsync(user, second.Next);

        Assert.Equal("newer", first.Titles[0]);
        Assert.Equal(["older"], third.Titles);
        Assert.Equal(["tie-a", "tie-b", "tie-c"], first.Titles.Skip(1).Concat(second.Titles).Order(StringComparer.Ordinal));
        Assert.Null(third.Next);
    }
}
