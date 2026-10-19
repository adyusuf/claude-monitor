using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ClaudeMonitor.Api.Tests.Infrastructure;

/// <summary>A project with its people, one application and a component and a release, for the issue tests; every call goes through the public API.</summary>
public sealed record IssueTeam(TestUser Owner, TestUser Contributor, TestUser Reviewer, TestUser Viewer, Guid Project, Guid Application, Guid Component, Guid Release);

public static class IssueKit
{
    public static async Task<IssueTeam> TeamAsync(ApiFactory api, object? projectBody = null)
    {
        var owner = await api.NewClient().SignedUpAsync("is-owner");
        var contributor = await RemoteKit.MemberAsync(api, owner, "is-contrib");
        var reviewer = await RemoteKit.MemberAsync(api, owner, "is-review");
        var viewer = await RemoteKit.MemberAsync(api, owner, "is-viewer", "viewer");
        var created = projectBody is null
            ? await ProjectKit.CreateProjectAsync(owner, "Tracker")
            : await Created(await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/projects", projectBody));
        var project = created.GetProperty("id").GetGuid();
        (await owner.SendAsync(HttpMethod.Put, $"/api/projects/{project}/members/{contributor.Id}", new { role = "contributor" })).EnsureSuccessStatusCode();
        (await owner.SendAsync(HttpMethod.Put, $"/api/projects/{project}/members/{reviewer.Id}", new { role = "reviewer" })).EnsureSuccessStatusCode();
        var app = (await ProjectKit.CreateApplicationAsync(owner, project, "Web")).GetProperty("id").GetGuid();
        var component = (await Created(await owner.PostAsync($"/api/applications/{app}/components", new { name = "Cart" }))).GetProperty("id").GetGuid();
        var release = (await Created(await owner.PostAsync($"/api/applications/{app}/releases", new { name = "1.0.0" }))).GetProperty("id").GetGuid();
        return new IssueTeam(owner, contributor, reviewer, viewer, project, app, component, release);
    }

    public static async Task<JsonElement> Created(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Creates an issue and returns its id.</summary>
    public static async Task<Guid> IssueAsync(TestUser user, Guid project, object body) =>
        (await Created(await user.PostAsync($"/api/projects/{project}/issues", body))).GetProperty("issue").GetProperty("id").GetGuid();

    public static Task<Guid> IssueAsync(TestUser user, Guid project, string title, string type = "story", object? extra = null) =>
        IssueAsync(user, project, Merge(new Dictionary<string, object?> { ["title"] = title, ["type"] = type }, extra));

    /// <summary>The body as a dictionary with the extra fields laid over it.</summary>
    public static Dictionary<string, object?> Merge(Dictionary<string, object?> body, object? extra)
    {
        if (extra is not null)
        {
            foreach (var p in JsonSerializer.SerializeToElement(extra, TestUser.Json).EnumerateObject()) body[p.Name] = p.Value;
        }

        return body;
    }

    public static async Task<JsonElement> ViewAsync(TestUser user, Guid issue) => await user.GetJsonAsync($"/api/issues/{issue}");

    public static async Task<List<string?>> TitlesAsync(TestUser user, string url) =>
        (await user.GetJsonAsync(url)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()).ToList();
}
