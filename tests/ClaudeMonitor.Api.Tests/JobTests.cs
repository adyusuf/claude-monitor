using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Named commands the target's owner approved once (ADR-0005): proposing, approving, running and retiring them.</summary>
[Collection(ApiGroup.Name)]
public sealed class JobTests(ApiFactory api)
{
    private static readonly string[] Suite = ["/opt/ci/run-tests", "--suite", "unit"];

    private static JobProposal Propose(Guid target, string name = "Run tests", string[]? argv = null, int timeout = 300, string? reason = "needs a test run") =>
        new(target, name, argv ?? Suite, "/opt/ci", timeout, reason);

    private static RunCreate JobRun(Guid target, Guid? job, string[]? argv = null, string? cwd = "/opt/ci", int timeout = 120) =>
        RemoteKit.Argv(target, RemoteKit.NewKey(), argv ?? Suite, cwd, timeout) with { JobId = job };

    private static async Task<JobView> ProposeAsync(TestAgent requester, JobProposal job)
    {
        var response = await requester.Http.PostAsJsonAsync("/api/agent/jobs", job, TestUser.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JobView>(TestUser.Json))!;
    }

    private static async Task<JobView> ActiveJobAsync(RemoteTeam team, JobProposal? job = null)
    {
        var proposed = await ProposeAsync(team.Requester, job ?? Propose(team.TargetId));
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/jobs/{proposed.Id}/approve", new { })).StatusCode);
        return proposed;
    }

    private async Task<MachineJob> JobRowAsync(Guid id)
    {
        await using var db = api.Db();
        return await db.MachineJobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }

    [Fact]
    public async Task A_proposal_is_only_a_proposal_and_a_run_by_its_id_waits_until_the_owner_approves_it()
    {
        var team = await RemoteKit.TeamAsync(api);
        var job = await ProposeAsync(team.Requester, Propose(team.TargetId));
        Assert.Equal((JobStatuses.Proposed, "Run tests"), (job.Status, job.Name));
        Assert.Equal(Suite, job.Argv);
        var row = await JobRowAsync(job.Id);
        Assert.Equal((team.Owner.Id, team.Admin.Id, team.RequesterId), (row.OwnerUserId, row.ProposedByUserId, row.ProposedByAgentId));

        var created = await RemoteKit.CreateAsync(team.Requester, JobRun(team.TargetId, job.Id));
        Assert.Equal(RunStatuses.PendingApproval, created.Status);
        Assert.Null((await RemoteKit.RunRowAsync(api, created.Id)).JobId);
    }

    [Fact]
    public async Task A_proposal_the_matcher_refuses_a_bad_name_or_a_missing_target_is_refused()
    {
        var team = await RemoteKit.TeamAsync(api);
        var t = team.TargetId;
        foreach (var name in new[] { "", "   ", "a\nb", new string('n', 101) })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs", Propose(t, name), TestUser.Json)).StatusCode);
        }

        var shell = await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs", Propose(t, "sh", ["/bin/sh", "-c", "x"]), TestUser.Json);
        Assert.Equal(HttpStatusCode.BadRequest, shell.StatusCode);
        var body = await shell.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((RemoteErrors.BadTemplate, GrantErrors.NeverGrantable), (body.GetProperty("title").GetString(), body.GetProperty("detail").GetString()));
        var exec = await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs", Propose(t, "find", ["/usr/bin/find", "/tmp", "-exec"]), TestUser.Json);
        Assert.Equal(HttpStatusCode.BadRequest, exec.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs", Propose(t, "r", reason: new string('r', 501)), TestUser.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs", Propose(Guid.NewGuid()), TestUser.Json)).StatusCode);
        await RemoteKit.SetSwitchAsync(team.Admin, false);
        await RemoteKit.AssertProblemAsync(await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs", Propose(t), TestUser.Json),
            HttpStatusCode.Conflict, RemoteErrors.Disabled);
        await using var db = api.Db();
        Assert.False(await db.MachineJobs.AnyAsync(j => j.TargetAgentId == team.TargetId));
    }

    [Fact]
    public async Task A_job_name_is_unique_per_target_ignoring_case_and_accents_until_the_job_is_retired()
    {
        var team = await RemoteKit.TeamAsync(api);
        var first = await ProposeAsync(team.Requester, Propose(team.TargetId, "Run Tests"));
        foreach (var same in new[] { "run tests", "  RUN TESTS ", "Rün Tests" })
        {
            await RemoteKit.AssertProblemAsync(await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs", Propose(team.TargetId, same), TestUser.Json),
                HttpStatusCode.Conflict, "job_name_taken");
        }

        var spare = await team.Owner.ConnectAgentAsync(team.WorkspaceId);
        await RemoteKit.ProfileAsync(spare, ExecLevels.Argv);
        Assert.Equal(JobStatuses.Proposed, (await ProposeAsync(team.Requester, Propose(spare.Tokens.AgentId, "Run Tests"))).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/jobs/{first.Id}/retire")).StatusCode);
        Assert.Equal(JobStatuses.Proposed, (await ProposeAsync(team.Requester, Propose(team.TargetId, "run tests"))).Status);
    }

    [Fact]
    public async Task At_most_ten_proposals_wait_for_one_target()
    {
        var team = await RemoteKit.TeamAsync(api);
        for (var i = 0; i < 10; i++) await ProposeAsync(team.Requester, Propose(team.TargetId, "job " + i));
        await RemoteKit.AssertProblemAsync(await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs", Propose(team.TargetId, "job 11"), TestUser.Json),
            HttpStatusCode.Conflict, RemoteErrors.TargetBusy);
    }

    [Fact]
    public async Task Only_the_owner_approves_and_needs_a_recent_sign_in_or_a_code()
    {
        var team = await RemoteKit.TeamAsync(api);
        var job = await ProposeAsync(team.Requester, Propose(team.TargetId));
        foreach (var notOwner in new[] { team.Admin, team.Other })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await notOwner.PostAsync($"/api/jobs/{job.Id}/approve", new { })).StatusCode);
        }

        api.Clock.Advance(TimeSpan.FromMinutes(11));
        var refused = await team.Owner.PostAsync($"/api/jobs/{job.Id}/approve", new { });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("reauth_required", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Equal(JobStatuses.Proposed, (await JobRowAsync(job.Id)).Status);

        var again = api.NewClient();
        (await again.PostAsync("/api/auth/login", new { email = team.Owner.Email, password = TestUser.Password })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await again.PostAsync($"/api/jobs/{job.Id}/approve", new { })).StatusCode);
        var row = await JobRowAsync(job.Id);
        Assert.Equal((JobStatuses.Active, team.Owner.Id), (row.Status, row.DecidedBy));
        await RemoteKit.AssertProblemAsync(await again.PostAsync($"/api/jobs/{job.Id}/approve", new { }), HttpStatusCode.Conflict, RemoteErrors.NotPending);
    }

    [Fact]
    public async Task An_old_session_approves_a_job_with_a_valid_two_step_code_only()
    {
        var team = await RemoteKit.TeamAsync(api);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        var job = await ProposeAsync(team.Requester, Propose(team.TargetId));
        api.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.Forbidden, (await team.Owner.PostAsync($"/api/jobs/{job.Id}/approve", new { code = "000000" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await team.Owner.PostAsync($"/api/jobs/{job.Id}/approve", new { code = RemoteKit.TotpNow(api, secret) })).StatusCode);
    }

    [Fact]
    public async Task A_run_by_job_id_with_exactly_the_jobs_command_is_approved_at_once_by_the_job()
    {
        var team = await RemoteKit.TeamAsync(api);
        var job = await ActiveJobAsync(team);
        var created = await RemoteKit.CreateAsync(team.Requester, JobRun(team.TargetId, job.Id));
        Assert.Equal((RunStatuses.Approved, null), (created.Status, created.GrantId));
        var row = await RemoteKit.RunRowAsync(api, created.Id);
        Assert.Equal((job.Id, team.Owner.Id, RunStatuses.Approved), (row.JobId, row.DecidedBy, row.Status));
        var shown = await team.Requester.Http.GetFromJsonAsync<RunView>($"/api/agent/runs/{created.Id}", TestUser.Json);
        Assert.Equal(job.Id, shown!.JobId);
        var atTheLimit = await RemoteKit.CreateAsync(team.Requester, JobRun(team.TargetId, job.Id, timeout: 300));
        Assert.Equal(RunStatuses.Approved, atTheLimit.Status);
    }

    [Fact]
    public async Task A_run_by_job_id_that_differs_in_any_way_waits_for_the_owner_and_is_not_tied_to_the_job()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Shell);
        var job = await ActiveJobAsync(team);
        var spare = await team.Owner.ConnectAgentAsync(team.WorkspaceId);
        await RemoteKit.ProfileAsync(spare, ExecLevels.Argv);
        var differing = new[]
        {
            JobRun(team.TargetId, job.Id, ["/opt/ci/run-tests", "--suite", "all"]),
            JobRun(team.TargetId, job.Id, ["/opt/ci/run-tests", "--suite"]),
            JobRun(team.TargetId, job.Id, ["/opt/ci/run-tests", "--suite", "unit", "--extra"]),
            JobRun(team.TargetId, job.Id, cwd: "/opt"),
            JobRun(team.TargetId, job.Id, cwd: null),
            JobRun(team.TargetId, job.Id, timeout: 301),
            JobRun(spare.Tokens.AgentId, job.Id),
            JobRun(team.TargetId, Guid.NewGuid()),
            JobRun(team.TargetId, null),
            RemoteKit.Shell(team.TargetId, RemoteKit.NewKey(), "/opt/ci/run-tests --suite unit") with { JobId = job.Id },
        };
        foreach (var req in differing)
        {
            var created = await RemoteKit.CreateAsync(team.Requester, req);
            Assert.Equal(RunStatuses.PendingApproval, created.Status);
            Assert.Null((await RemoteKit.RunRowAsync(api, created.Id)).JobId);
        }
    }

    [Fact]
    public async Task A_job_with_a_free_placeholder_still_needs_a_per_call_approval_for_every_run()
    {
        var team = await RemoteKit.TeamAsync(api);
        var job = await ActiveJobAsync(team, Propose(team.TargetId, "by ref", ["/opt/ci/run-tests", "--ref", "{word}"]));
        foreach (var value in new[] { "main", "{word}" })
        {
            var created = await RemoteKit.CreateAsync(team.Requester, JobRun(team.TargetId, job.Id, ["/opt/ci/run-tests", "--ref", value]));
            Assert.Equal(RunStatuses.PendingApproval, created.Status);
        }
    }

    [Fact]
    public async Task Retiring_a_job_cancels_the_runs_it_approved_and_approves_nothing_more()
    {
        var team = await RemoteKit.TeamAsync(api);
        var job = await ActiveJobAsync(team);
        var open = (await RemoteKit.CreateAsync(team.Requester, JobRun(team.TargetId, job.Id))).Id;
        var waiting = (await RemoteKit.CreateAsync(team.Requester, JobRun(team.TargetId, null))).Id;
        using var targetStream = await RemoteKit.OpenAsync(team.Target.Http, "/api/agent/stream");
        await RemoteKit.NextEventAsync(targetStream, AgentStreamEvents.Ready);

        foreach (var notOwner in new[] { team.Admin, team.Other })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await notOwner.PostAsync($"/api/jobs/{job.Id}/retire")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/jobs/{job.Id}/retire")).StatusCode);
        var row = await JobRowAsync(job.Id);
        Assert.Equal(JobStatuses.Retired, row.Status);
        Assert.NotNull(row.RetiredAt);
        Assert.Equal(RunStatuses.Cancelled, await RemoteKit.StatusOfAsync(api, open));
        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, waiting));
        Assert.Equal(open, (await RemoteKit.NextEventAsync(targetStream, AgentStreamEvents.RunCancel)).GetProperty("id").GetGuid());
        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync($"/api/jobs/{job.Id}/retire"), HttpStatusCode.Conflict, RemoteErrors.NotPending);
        var after = await RemoteKit.CreateAsync(team.Requester, JobRun(team.TargetId, job.Id));
        Assert.Equal(RunStatuses.PendingApproval, after.Status);
        Assert.Null((await RemoteKit.RunRowAsync(api, after.Id)).JobId);
    }

    [Fact]
    public async Task A_denied_job_never_runs_and_a_denial_is_the_owners_alone()
    {
        var team = await RemoteKit.TeamAsync(api);
        var job = await ProposeAsync(team.Requester, Propose(team.TargetId));
        Assert.Equal(HttpStatusCode.NotFound, (await team.Other.PostAsync($"/api/jobs/{job.Id}/deny")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/jobs/{job.Id}/deny")).StatusCode);
        Assert.Equal(JobStatuses.Denied, (await JobRowAsync(job.Id)).Status);
        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync($"/api/jobs/{job.Id}/deny"), HttpStatusCode.Conflict, RemoteErrors.NotPending);
        Assert.Equal(RunStatuses.PendingApproval, (await RemoteKit.CreateAsync(team.Requester, JobRun(team.TargetId, job.Id))).Status);
    }

    [Fact]
    public async Task Jobs_are_listed_to_agents_and_members_and_the_reason_only_to_the_owner_and_the_proposer()
    {
        var team = await RemoteKit.TeamAsync(api);
        var job = await ActiveJobAsync(team);
        var listed = await team.Requester.Http.GetFromJsonAsync<List<JobView>>($"/api/agent/jobs?target={team.TargetId}", TestUser.Json);
        Assert.Equal(job.Id, Assert.Single(listed!).Id);
        Assert.Empty((await team.Requester.Http.GetFromJsonAsync<List<JobView>>($"/api/agent/jobs?target={Guid.NewGuid()}", TestUser.Json))!);

        foreach (var (seer, sees) in new[] { (team.Owner, true), (team.Admin, true), (team.Other, false) })
        {
            var web = await seer.GetJsonAsync($"/api/agents/{team.TargetId}/jobs");
            var one = Assert.Single(web.EnumerateArray());
            Assert.Equal(sees ? "needs a test run" : null, one.GetProperty("reason").GetString());
            Assert.Equal("Run tests", one.GetProperty("name").GetString());
        }

        var outsider = await api.NewClient().SignedUpAsync("job-outsider");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Http.GetAsync($"/api/agents/{team.TargetId}/jobs")).StatusCode);
    }
}
