using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>Setup and reading helpers for the managed project tests; every call goes through the public API.</summary>
public static class ProjectKit
{
    public static object Project(string name, object? owner = null) => new
    {
        name,
        purpose = "Ship the thing",
        ownerUserId = owner,
        sponsor = "Head of delivery",
        stakeholders = new[] { new { name = "Ada", role = "Reviewer" } },
        budgetAmount = 12500.5m,
        budgetCurrency = "EUR",
        startDate = "2026-11-01",
        endDate = "2027-03-31",
        itemTypes = new[] { "epic", "story", "bug" },
        priorityScale = "moscow",
        estimationUnit = "hours",
        sprintLengthDays = 10,
        definitionOfReady = new[] { "Has acceptance criteria" },
        definitionOfDone = new[] { "Reviewed", "Deployed to test" },
    };

    public static object Application(string name, object? techOwner = null) => new
    {
        name,
        description = "The storefront",
        kind = "web",
        repoUrl = "https://git.example.invalid/team/shop.git",
        environments = new[] { new { name = "dev", url = (string?)"https://dev.shop.example.invalid" }, new { name = "prod", url = (string?)null } },
        techStack = new[] { "React", ".NET" },
        versioningScheme = "semver",
        techOwnerUserId = techOwner,
        nonFunctionalTargets = new[] { new { kind = "sla", target = "99.9% a month" } },
    };

    public static async Task<JsonElement> CreateProjectAsync(TestUser user, string name, object? owner = null, Guid? workspace = null)
    {
        var response = await user.PostAsync($"/api/workspaces/{workspace ?? user.WorkspaceId}/projects", Project(name, owner));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<JsonElement> CreateApplicationAsync(TestUser user, Guid projectId, string name, object? techOwner = null)
    {
        var response = await user.PostAsync($"/api/projects/{projectId}/applications", Application(name, techOwner));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The first error code of a 400 answer, e.g. <c>name_taken</c>.</summary>
    public static async Task<(string Field, string Code)> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var first = body.GetProperty("errors").EnumerateObject().First();
        return (first.Name, first.Value[0].GetString()!);
    }
}
