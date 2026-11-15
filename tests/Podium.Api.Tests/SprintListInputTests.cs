using System.Net;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>What the sprint list accepts in its query: a cursor that is not a sprint number and an unknown status are refused, not ignored.</summary>
[Collection(ApiGroup.Name)]
public sealed class SprintListInputTests(ApiFactory api)
{
    [Fact]
    public async Task A_cursor_that_is_not_a_sprint_number_and_an_unknown_status_are_refused_not_ignored()
    {
        var t = await IssueKit.TeamAsync(api);
        await SprintKit.SprintAsync(t.Owner, t.Project);
        Task<HttpResponseMessage> List(string query) => t.Owner.Http.GetAsync($"/api/projects/{t.Project}/sprints?{query}");

        Assert.Equal(("cursor", "invalid_value"), await ProjectKit.ProblemAsync(await List("cursor=not-a-number")));
        Assert.Equal(("status", "invalid_value"), await ProjectKit.ProblemAsync(await List("status=finished")));
        Assert.Equal(HttpStatusCode.OK, (await List("cursor=5&status=planned")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await List("status=closed")).StatusCode);
    }
}
