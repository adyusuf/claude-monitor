using System.Net;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>Text PostgreSQL cannot store (a NUL) in a prompt or a deny reason is a 400 with the field named, never a 500, and nothing is saved (#7).</summary>
[Collection(ApiGroup.Name)]
public sealed class CommandUnstorableTextTests(ApiFactory api)
{
    [Fact]
    public async Task A_prompt_with_a_NUL_is_refused_as_an_invalid_body_and_nothing_is_queued()
    {
        var team = await SessionKit.TeamAsync(api);

        var response = await team.Owner.PostAsync($"/api/sessions/{team.SessionId}/commands", new { kind = "prompt", body = "run the tests\0 now" });

        Assert.Equal(("body", "invalid_body"), await ProjectKit.ProblemAsync(response));
        Assert.Empty((await team.Admin.GetJsonAsync($"/api/sessions/{team.SessionId}/commands")).EnumerateArray());
    }

    [Fact]
    public async Task A_prompt_with_a_line_break_and_a_tab_is_still_accepted()
    {
        var team = await SessionKit.TeamAsync(api);

        var response = await team.Owner.PostAsync($"/api/sessions/{team.SessionId}/commands", new { kind = "prompt", body = "line one\n\tline two" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_deny_reason_with_a_NUL_is_refused_as_an_invalid_reason_and_the_request_stays_open()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "nul-reason");
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", new { command = "make build" });

        var response = await w.Owner.PostAsync($"/api/permission-requests/{request.Id}/answer", new { decision = PermissionDecisions.Deny, reason = "no\0way" });

        Assert.Equal(("reason", "invalid_reason"), await ProjectKit.ProblemAsync(response));
        Assert.Equal("open", (await ApprovalRuleKit.RequestRowAsync(api, request.Id)).Status);
    }
}
