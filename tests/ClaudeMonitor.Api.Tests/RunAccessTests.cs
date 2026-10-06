using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Who may read a run and its output, who sees its command on the web, and who may cancel it (ADR-0005).</summary>
[Collection(ApiGroup.Name)]
public sealed class RunAccessTests(ApiFactory api)
{
    private static async Task<(RunCreate Req, Guid Id)> AskAsync(RemoteTeam team, string[]? argv = null, string? reason = "because tests")
    {
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), argv ?? ["/bin/echo", "TOP-SECRET-ARG"], "/tmp", 30, reason);
        return (req, (await RemoteKit.CreateAsync(team.Requester, req)).Id);
    }

    [Fact]
    public async Task The_requesters_agent_reads_its_own_run_and_no_other_agent_can()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (req, id) = await AskAsync(team);
        var view = await team.Requester.Http.GetFromJsonAsync<RunView>($"/api/agent/runs/{id}", TestUser.Json);
        Assert.Equal((id, team.TargetId, RunModes.Argv, RunStatuses.PendingApproval), (view!.Id, view.TargetAgentId, view.Mode, view.Status));
        Assert.Equal(req.Argv, view.Argv);
        Assert.Equal("laptop-1", view.TargetHostname);

        var anotherOfTheRequester = await team.Admin.ConnectAgentAsync();
        Assert.Equal(HttpStatusCode.OK, (await anotherOfTheRequester.Http.GetAsync($"/api/agent/runs/{id}")).StatusCode);
        var member = await team.Other.ConnectAgentAsync(team.WorkspaceId);
        foreach (var agent in new[] { team.Target, member })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await agent.Http.GetAsync($"/api/agent/runs/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await agent.Http.GetAsync($"/api/agent/runs/{id}/output")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await team.Requester.Http.GetAsync($"/api/agent/runs/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Output_is_read_on_the_web_by_the_requester_and_the_owner_only()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (req, id) = await AskAsync(team);
        (await RemoteKit.ApproveAsync(team.Owner, id, req)).EnsureSuccessStatusCode();
        (await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate("running"))).EnsureSuccessStatusCode();
        (await team.Target.Http.PostAsJsonAsync($"/api/agent/runs/{id}/output", new[] { new RunOutputChunk(0, "stdout", "result-line\n") }, TestUser.Json))
            .EnsureSuccessStatusCode();

        foreach (var reader in new[] { team.Admin, team.Owner })
        {
            var page = await reader.Http.GetFromJsonAsync<RunOutputPage>($"/api/runs/{id}/output", TestUser.Json);
            Assert.Equal("result-line\n", Assert.Single(page!.Chunks).Body);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await team.Other.Http.GetAsync($"/api/runs/{id}/output")).StatusCode);
        var outsider = await api.NewClient().SignedUpAsync("run-outsider");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Http.GetAsync($"/api/runs/{id}/output")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await team.Admin.Http.GetAsync($"/api/runs/{Guid.NewGuid()}/output")).StatusCode);
    }

    [Fact]
    public async Task A_third_member_sees_who_ran_what_where_but_never_the_command_the_reason_or_the_error()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (_, id) = await AskAsync(team);
        (await team.Owner.PostAsync($"/api/runs/{id}/deny", new { reason = "private refusal" })).EnsureSuccessStatusCode();

        foreach (var seer in new[] { team.Admin, team.Owner })
        {
            var run = await RemoteKit.WebRunAsync(seer, team.WorkspaceId, id);
            Assert.True(run.GetProperty("visible").GetBoolean());
            Assert.Equal("TOP-SECRET-ARG", run.GetProperty("argv")[1].GetString());
            Assert.Equal("/tmp", run.GetProperty("cwd").GetString());
            Assert.Equal("because tests", run.GetProperty("reason").GetString());
            Assert.Equal("private refusal", run.GetProperty("error").GetString());
        }

        var third = await RemoteKit.WebRunAsync(team.Other, team.WorkspaceId, id);
        Assert.False(third.GetProperty("visible").GetBoolean());
        foreach (var hidden in new[] { "argv", "shellCommand", "cwd", "reason", "error" })
        {
            Assert.Equal(JsonValueKind.Null, third.GetProperty(hidden).ValueKind);
        }

        Assert.DoesNotContain("TOP-SECRET-ARG", third.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("denied", third.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(third.GetProperty("requesterHostname").GetString()));
        Assert.False(string.IsNullOrEmpty(third.GetProperty("targetHostname").GetString()));
        var single = await team.Other.GetJsonAsync($"/api/runs/{id}");
        Assert.False(single.GetProperty("visible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, single.GetProperty("argv").ValueKind);
    }

    [Fact]
    public async Task The_run_list_and_a_single_run_need_membership_and_a_viewer_may_read_them()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (_, id) = await AskAsync(team);
        var viewer = await RemoteKit.MemberAsync(api, team.Admin, "run-reader", "viewer");
        Assert.Equal(HttpStatusCode.OK, (await viewer.Http.GetAsync($"/api/workspaces/{team.WorkspaceId}/runs")).StatusCode);
        Assert.False((await viewer.GetJsonAsync($"/api/runs/{id}")).GetProperty("visible").GetBoolean());
        var outsider = await api.NewClient().SignedUpAsync("run-list-outsider");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Http.GetAsync($"/api/workspaces/{team.WorkspaceId}/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Http.GetAsync($"/api/runs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await team.Admin.Http.GetAsync($"/api/runs/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task A_shell_run_or_an_interpreter_asks_for_the_red_notice_and_a_plain_program_does_not()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Shell);
        var plain = (await AskAsync(team)).Id;
        var interpreter = (await AskAsync(team, ["/usr/bin/python3.12", "-c", "x"])).Id;
        var shell = (await RemoteKit.CreateAsync(team.Requester, RemoteKit.Shell(team.TargetId, RemoteKit.NewKey()))).Id;
        Assert.False((await RemoteKit.WebRunAsync(team.Owner, team.WorkspaceId, plain)).GetProperty("interpreter").GetBoolean());
        Assert.True((await RemoteKit.WebRunAsync(team.Owner, team.WorkspaceId, interpreter)).GetProperty("interpreter").GetBoolean());
        Assert.True((await RemoteKit.WebRunAsync(team.Owner, team.WorkspaceId, shell)).GetProperty("interpreter").GetBoolean());
    }

    [Fact]
    public async Task A_waiting_run_says_when_its_requester_read_output_from_a_machine_in_the_last_ten_minutes()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (earlierReq, earlier) = await AskAsync(team);
        var (_, calm) = await AskAsync(team);
        Assert.False((await RemoteKit.WebRunAsync(team.Owner, team.WorkspaceId, calm)).GetProperty("recentOutput").GetBoolean());
        (await RemoteKit.ApproveAsync(team.Owner, earlier, earlierReq)).EnsureSuccessStatusCode();
        (await team.Target.Http.PostAsJsonAsync($"/api/agent/runs/{earlier}/output", new[] { new RunOutputChunk(0, "stdout", "output") }, TestUser.Json))
            .EnsureSuccessStatusCode();
        (await team.Admin.PostAsync($"/api/runs/{earlier}/cancel")).EnsureSuccessStatusCode();

        Assert.True((await RemoteKit.WebRunAsync(team.Owner, team.WorkspaceId, calm)).GetProperty("recentOutput").GetBoolean());
        api.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.False((await RemoteKit.WebRunAsync(team.Owner, team.WorkspaceId, calm)).GetProperty("recentOutput").GetBoolean());
    }

    [Fact]
    public async Task The_requester_and_the_owner_cancel_a_waiting_run_but_a_third_member_cannot()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (_, byAgent) = await AskAsync(team);
        var (_, byOwner) = await AskAsync(team);
        var (_, untouched) = await AskAsync(team);
        Assert.Equal(HttpStatusCode.NotFound, (await team.Other.PostAsync($"/api/runs/{untouched}/cancel")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Requester.Http.PostAsync($"/api/agent/runs/{byAgent}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/runs/{byOwner}/cancel")).StatusCode);
        var member = await team.Other.ConnectAgentAsync(team.WorkspaceId);
        Assert.Equal(HttpStatusCode.NotFound, (await member.Http.PostAsync($"/api/agent/runs/{untouched}/cancel", null)).StatusCode);

        Assert.Equal(RunStatuses.Cancelled, await RemoteKit.StatusOfAsync(api, byAgent));
        Assert.Equal(RunStatuses.Cancelled, await RemoteKit.StatusOfAsync(api, byOwner));
        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, untouched));
        Assert.NotNull((await RemoteKit.RunRowAsync(api, byAgent)).EndedAt);
        await RemoteKit.AssertProblemAsync(await team.Admin.PostAsync($"/api/runs/{byAgent}/cancel"), HttpStatusCode.Conflict, RemoteErrors.NotPending);
        Assert.Equal(HttpStatusCode.NotFound, (await team.Admin.PostAsync($"/api/runs/{Guid.NewGuid()}/cancel")).StatusCode);
    }

    [Fact]
    public async Task Cancelling_a_running_run_tells_the_target_to_kill_it_and_a_waiting_one_is_not_sent_anywhere()
    {
        var team = await RemoteKit.TeamAsync(api);
        using var targetStream = await RemoteKit.OpenAsync(team.Target.Http, "/api/agent/stream");
        await RemoteKit.NextEventAsync(targetStream, AgentStreamEvents.Ready);
        var (_, waiting) = await AskAsync(team);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Admin.PostAsync($"/api/runs/{waiting}/cancel")).StatusCode);
        var (req, id) = await AskAsync(team);
        (await RemoteKit.ApproveAsync(team.Owner, id, req)).EnsureSuccessStatusCode();
        (await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate("running"))).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await team.Requester.Http.PostAsync($"/api/agent/runs/{id}/cancel", null)).StatusCode);
        var cancel = await RemoteKit.NextEventAsync(targetStream, AgentStreamEvents.RunCancel);
        Assert.Equal(id, cancel.GetProperty("id").GetGuid()); // the first cancel the target hears is the running run's, not the waiting one's
        Assert.Equal(HttpStatusCode.Conflict, (await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate("failed", 1))).StatusCode);
    }
}
