using System.Net;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>A project PUT may carry the updatedAt the form was loaded with: a project that changed since is refused with 409 stale_update; without it the old last-write-wins stays.</summary>
[Collection(ApiGroup.Name)]
public sealed class ProjectStaleUpdateTests(ApiFactory api)
{
    private static async Task<(Guid Id, string UpdatedAt)> CreateAsync(TestUser owner, string name)
    {
        var id = (await ProjectKit.CreateProjectAsync(owner, name)).GetProperty("id").GetGuid();
        return (id, (await owner.GetJsonAsync($"/api/projects/{id}")).GetProperty("updatedAt").GetString()!);
    }

    private static Dictionary<string, object?> Body(string name, string? expected) =>
        IssueKit.Merge(System.Text.Json.JsonSerializer.SerializeToElement(ProjectKit.Project(name), TestUser.Json).EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value), expected is null ? null : new { expectedUpdatedAt = expected });

    [Fact]
    public async Task A_form_older_than_the_stored_project_is_refused_with_stale_update_and_changes_nothing()
    {
        var owner = await api.NewClient().SignedUpAsync("pj-stale");
        var (id, loaded) = await CreateAsync(owner, "Stale base");
        api.Clock.Advance(TimeSpan.FromMinutes(1)); // a later edit gets a later updatedAt
        (await owner.SendAsync(HttpMethod.Put, $"/api/projects/{id}", Body("First edit", loaded))).EnsureSuccessStatusCode();

        var late = await owner.SendAsync(HttpMethod.Put, $"/api/projects/{id}", Body("Late edit", loaded));

        await JiraWriteKit.AssertRefusedAsync(late, HttpStatusCode.Conflict, "stale_update");
        Assert.Equal("First edit", (await owner.GetJsonAsync($"/api/projects/{id}")).GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_form_that_names_the_current_version_is_accepted_and_the_new_version_is_then_the_one_to_name()
    {
        var owner = await api.NewClient().SignedUpAsync("pj-fresh");
        var (id, loaded) = await CreateAsync(owner, "Fresh base");
        api.Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Put, $"/api/projects/{id}", Body("Fresh one", loaded))).StatusCode);
        var next = (await owner.GetJsonAsync($"/api/projects/{id}")).GetProperty("updatedAt").GetString()!;

        Assert.NotEqual(loaded, next);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Put, $"/api/projects/{id}", Body("Fresh two", next))).StatusCode);
    }

    [Fact]
    public async Task A_client_that_sends_no_version_keeps_the_old_last_write_wins_behaviour()
    {
        var owner = await api.NewClient().SignedUpAsync("pj-old");
        var (id, _) = await CreateAsync(owner, "Old base");
        (await owner.SendAsync(HttpMethod.Put, $"/api/projects/{id}", Body("Somebody else", null))).EnsureSuccessStatusCode();

        var response = await owner.SendAsync(HttpMethod.Put, $"/api/projects/{id}", Body("Old client", null));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Old client", (await owner.GetJsonAsync($"/api/projects/{id}")).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Two_forms_opened_on_the_same_version_cannot_both_win()
    {
        var owner = await api.NewClient().SignedUpAsync("pj-race");
        var (id, loaded) = await CreateAsync(owner, "Race base");
        api.Clock.Advance(TimeSpan.FromMinutes(1));

        var both = await Task.WhenAll(
            owner.SendAsync(HttpMethod.Put, $"/api/projects/{id}", Body("Race one", loaded)),
            owner.SendAsync(HttpMethod.Put, $"/api/projects/{id}", Body("Race two", loaded)));

        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Conflict], both.Select(r => r.StatusCode).Order());
    }
}
