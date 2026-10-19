using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ClaudeMonitor.Api.Tests.Infrastructure;

/// <summary>Helpers for the sprint tests; every call goes through the public API.</summary>
public static class SprintKit
{
    public static async Task<Guid> SprintAsync(TestUser user, Guid project, object? body = null)
    {
        var response = await user.PostAsync($"/api/projects/{project}/sprints", body ?? new { goal = "Ship it", startDate = "2026-11-02", endDate = "2026-11-13" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sprint").GetProperty("id").GetGuid();
    }

    public static async Task<JsonElement> ViewAsync(TestUser user, Guid sprint) => await user.GetJsonAsync($"/api/sprints/{sprint}");

    public static async Task<string> StatusAsync(TestUser user, Guid sprint) => (await ViewAsync(user, sprint)).GetProperty("sprint").GetProperty("status").GetString()!;

    public static Task<HttpResponseMessage> StartAsync(TestUser user, Guid sprint) => user.PostAsync($"/api/sprints/{sprint}/start");

    public static Task<HttpResponseMessage> CloseAsync(TestUser user, Guid sprint, Guid? moveTo = null) =>
        user.PostAsync($"/api/sprints/{sprint}/close", new { moveUnfinishedTo = moveTo });

    public static Task<HttpResponseMessage> AddAsync(TestUser user, Guid sprint, params Guid[] issues) =>
        user.PostAsync($"/api/sprints/{sprint}/issues", new { issueIds = issues });

    public static async Task<string> IssueStatusAsync(TestUser user, Guid issue) =>
        (await IssueKit.ViewAsync(user, issue)).GetProperty("issue").GetProperty("status").GetString()!;

    public static async Task<Guid?> IssueSprintAsync(TestUser user, Guid issue)
    {
        var sprint = (await IssueKit.ViewAsync(user, issue)).GetProperty("issue").GetProperty("sprintId");
        return sprint.ValueKind == JsonValueKind.Null ? null : sprint.GetGuid();
    }
}
