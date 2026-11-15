using System.Net;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>A null inside a list of objects is bad input (a 400 with the list's code), never an unhandled error (global #7).</summary>
[Collection(ApiGroup.Name)]
public sealed class NullListElementTests(ApiFactory api)
{
    private static readonly object?[] OneNull = [null];

    [Fact]
    public async Task A_null_stakeholder_is_refused_on_create_and_on_update()
    {
        var owner = await api.NewClient().SignedUpAsync("nl-project");
        var project = (await ProjectKit.CreateProjectAsync(owner, "Nulls")).GetProperty("id").GetGuid();
        var body = IssueKit.Merge(new() { ["name"] = "Other" }, new { stakeholders = OneNull });

        Assert.Equal(("stakeholders", "invalid_list"), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/projects", body)));
        Assert.Equal(("stakeholders", "invalid_list"), await ProjectKit.ProblemAsync(await owner.SendAsync(HttpMethod.Put, $"/api/projects/{project}", body)));
    }

    [Theory]
    [InlineData("environments")]
    [InlineData("nonFunctionalTargets")]
    public async Task A_null_environment_or_target_is_refused_on_create_and_on_update(string field)
    {
        var owner = await api.NewClient().SignedUpAsync("nl-app");
        var project = (await ProjectKit.CreateProjectAsync(owner, "Nulls")).GetProperty("id").GetGuid();
        var app = (await ProjectKit.CreateApplicationAsync(owner, project, "Web")).GetProperty("id").GetGuid();
        var body = IssueKit.Merge(new() { ["name"] = "Other" }, new Dictionary<string, object?> { [field] = OneNull });

        Assert.Equal((field, "invalid_list"), await ProjectKit.ProblemAsync(await owner.PostAsync($"/api/projects/{project}/applications", body)));
        Assert.Equal((field, "invalid_list"), await ProjectKit.ProblemAsync(await owner.SendAsync(HttpMethod.Put, $"/api/applications/{app}", body)));
    }

    [Fact]
    public async Task A_null_acceptance_criterion_is_refused_on_create_and_on_update()
    {
        var t = await IssueKit.TeamAsync(api);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Criteria");
        var body = new { title = "Criteria", acceptanceCriteria = OneNull };

        Assert.Equal(("acceptanceCriteria", "invalid_list"), await ProjectKit.ProblemAsync(await t.Owner.PostAsync($"/api/projects/{t.Project}/issues", body)));
        Assert.Equal(("acceptanceCriteria", "invalid_list"), await ProjectKit.ProblemAsync(await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{issue}", body)));
        Assert.Equal(HttpStatusCode.NoContent, (await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{issue}", new { title = "Criteria" })).StatusCode);
    }
}
