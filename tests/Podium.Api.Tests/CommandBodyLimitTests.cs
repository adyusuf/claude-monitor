using System.Net;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>The size of a prompt sent from the web: 10,000 characters are taken, one more is refused (the web has its own copy of this number).</summary>
[Collection(ApiGroup.Name)]
public sealed class CommandBodyLimitTests(ApiFactory api)
{
    [Fact]
    public async Task A_prompt_of_10000_characters_is_accepted()
    {
        var team = await SessionKit.TeamAsync(api);

        var response = await team.Owner.PostAsync($"/api/sessions/{team.SessionId}/commands", new { kind = "prompt", body = new string('a', 10_000) });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_prompt_of_10001_characters_is_refused_as_an_invalid_body_and_nothing_is_queued()
    {
        var team = await SessionKit.TeamAsync(api);

        var response = await team.Owner.PostAsync($"/api/sessions/{team.SessionId}/commands", new { kind = "prompt", body = new string('a', 10_001) });

        Assert.Equal(("body", "invalid_body"), await ProjectKit.ProblemAsync(response));
        Assert.Empty((await team.Admin.GetJsonAsync($"/api/sessions/{team.SessionId}/commands")).EnumerateArray());
    }
}
