using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Remote runs, grants, jobs, alerts and metrics are personal data: exported with the account, emptied on deletion (ADR-0005).</summary>
[Collection(ApiGroup.Name)]
public sealed class RemotePrivacyTests(ApiFactory api)
{
    private const string Marker = "EXPORT-MARKER";

    private sealed record World(RemoteTeam Team, TestUser Asker, TestAgent AskerAgent, Guid Finished, Guid Waiting, Guid Grant, Guid Job);

    private DateTimeOffset Now => api.Clock.GetUtcNow();

    /// <summary>The asker (a member) asks the target for a cancelled run with output, a waiting run, a grant and a job; the target has an alert and a metric.</summary>
    private async Task<World> BuildAsync()
    {
        var team = await RemoteKit.TeamAsync(api);
        var asker = await RemoteKit.MemberAsync(api, team.Admin, "rp-asker");
        var agent = await asker.ConnectAgentAsync(team.WorkspaceId);
        var finishedReq = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), ["/bin/echo", Marker], "/tmp/export-cwd", 30, "export reason");
        var finished = (await RemoteKit.CreateAsync(agent, finishedReq)).Id;
        (await asker.PostAsync($"/api/runs/{finished}/cancel")).EnsureSuccessStatusCode();
        var waiting = (await RemoteKit.CreateAsync(agent, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), ["/bin/echo", Marker + "-2"], "/tmp/export-cwd", 30, "second reason"))).Id;
        await using (var db = api.Db())
        {
            db.RemoteRunOutput.Add(new RemoteRunOutput { RunId = finished, Seq = 0, Stream = "stdout", Body = Marker + "-output", Bytes = 20, ReceivedAt = Now });
            await db.SaveChangesAsync();
            await db.RemoteRuns.Where(r => r.Id == finished).ExecuteUpdateAsync(s => s.SetProperty(r => r.Error, "export error").SetProperty(r => r.ResolvedExe, "/bin/echo"));
        }

        var grant = await RemoteKit.RequestGrantAsync(agent, RemoteKit.GrantAsk(team.TargetId, reason: "grant reason"));
        (await team.Owner.PostAsync($"/api/grants/{grant.Id}/approve", new { })).EnsureSuccessStatusCode();
        var proposal = new JobProposal(team.TargetId, "Export " + Guid.NewGuid().ToString("N")[..8], ["/opt/ci/run-tests", Marker], "/opt/ci", 60, "job reason");
        var job = (await (await agent.Http.PostAsJsonAsync("/api/agent/jobs", proposal, TestUser.Json)).Content.ReadFromJsonAsync<JobView>(TestUser.Json))!;
        (await team.Owner.PostAsync($"/api/jobs/{job.Id}/approve", new { })).EnsureSuccessStatusCode();
        (await team.Target.Http.PostAsJsonAsync("/api/agent/alerts", new[] { new AlertReport("disk", "/data", "open", 95, 90, Now) }, TestUser.Json)).EnsureSuccessStatusCode();
        (await team.Target.Http.PostAsJsonAsync("/api/agent/metrics", new MetricsReport([new MetricSample(Now, 33, 1, 2, [])]), TestUser.Json)).EnsureSuccessStatusCode();
        return new World(team, asker, agent, finished, waiting, grant.Id, job.Id);
    }

    private static async Task<JsonElement> ExportAsync(TestUser user)
    {
        var response = await user.Http.GetAsync("/api/me/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task The_export_holds_the_runs_grants_jobs_alerts_and_metrics_the_user_asked_for_or_owns()
    {
        var w = await BuildAsync();
        var asker = await ExportAsync(w.Asker);
        var runs = asker.GetProperty("remoteRuns").EnumerateArray().ToList();
        Assert.Equal(2, runs.Count);
        var finished = runs.Single(r => r.GetProperty("id").GetGuid() == w.Finished);
        Assert.Equal(("argv", "cancelled", "/tmp/export-cwd"), (finished.GetProperty("mode").GetString(), finished.GetProperty("status").GetString(), finished.GetProperty("cwd").GetString()));
        Assert.Equal(Marker, finished.GetProperty("argv")[1].GetString());
        Assert.Equal(Marker + "-output", Assert.Single(finished.GetProperty("output").EnumerateArray()).GetProperty("body").GetString());
        Assert.Equal(w.Grant, Assert.Single(asker.GetProperty("grants").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(w.Job, Assert.Single(asker.GetProperty("jobs").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Empty(asker.GetProperty("alerts").EnumerateArray());

        var owner = await ExportAsync(w.Team.Owner);
        Assert.Equal(2, owner.GetProperty("remoteRuns").GetArrayLength());
        Assert.Equal(w.Grant, Assert.Single(owner.GetProperty("grants").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(w.Job, Assert.Single(owner.GetProperty("jobs").EnumerateArray()).GetProperty("id").GetGuid());
        var alert = Assert.Single(owner.GetProperty("alerts").EnumerateArray());
        Assert.Equal(("disk", "/data"), (alert.GetProperty("kind").GetString(), alert.GetProperty("subject").GetString()));
        Assert.Equal(33d, Assert.Single(owner.GetProperty("metrics").EnumerateArray()).GetProperty("cpuPct").GetDouble());
    }

    [Fact]
    public async Task The_export_of_a_member_with_no_part_in_it_holds_none_of_it()
    {
        var w = await BuildAsync();
        var other = await ExportAsync(w.Team.Other);
        foreach (var name in new[] { "remoteRuns", "grants", "jobs", "alerts", "metrics" })
        {
            Assert.Empty(other.GetProperty(name).EnumerateArray());
        }

        Assert.DoesNotContain(Marker, other.ToString(), StringComparison.Ordinal);
    }

    private async Task AssertEmptiedAsync(World w)
    {
        await using var db = api.Db();
        var runs = await db.RemoteRuns.AsNoTracking().Where(r => r.Id == w.Finished || r.Id == w.Waiting).ToListAsync();
        Assert.Equal(2, runs.Count);
        Assert.All(runs, r =>
        {
            Assert.Null(r.Argv);
            Assert.Null(r.ShellCommand);
            Assert.Null(r.Cwd);
            Assert.Null(r.Reason);
            Assert.Null(r.Error);
            Assert.Null(r.ResolvedExe);
        });
        Assert.Equal(RunStatuses.Cancelled, runs.Single(r => r.Id == w.Waiting).Status);
        Assert.False(await db.RemoteRunOutput.AnyAsync(o => o.RunId == w.Finished));
        var grant = await db.MachineGrants.AsNoTracking().SingleAsync(g => g.Id == w.Grant);
        Assert.Equal(GrantStatuses.Revoked, grant.Status);
        Assert.Null(grant.Template);
        Assert.Null(grant.Cwd);
        Assert.Null(grant.Reason);
        Assert.NotEmpty(grant.TemplateHash);
        var job = await db.MachineJobs.AsNoTracking().SingleAsync(j => j.Id == w.Job);
        Assert.Equal(JobStatuses.Retired, job.Status);
        Assert.Null(job.Argv);
        Assert.Null(job.Cwd);
        Assert.Null(job.Reason);
    }

    [Fact]
    public async Task Deleting_the_target_owners_account_empties_every_command_template_and_output_around_it_and_removes_its_machine_data()
    {
        var w = await BuildAsync();
        var bystanders = await BuildAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await w.Team.Owner.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).StatusCode);

        await AssertEmptiedAsync(w);
        await using var db = api.Db();
        Assert.False(await db.MachineMetrics.AnyAsync(m => m.AgentId == w.Team.TargetId));
        Assert.False(await db.MachineAlerts.AnyAsync(a => a.AgentId == w.Team.TargetId));
        Assert.Equal(RunStatuses.PendingApproval, (await db.RemoteRuns.AsNoTracking().SingleAsync(r => r.Id == bystanders.Waiting)).Status);
        Assert.NotNull((await db.RemoteRuns.AsNoTracking().SingleAsync(r => r.Id == bystanders.Waiting)).Argv);
        Assert.Equal(GrantStatuses.Active, (await db.MachineGrants.AsNoTracking().SingleAsync(g => g.Id == bystanders.Grant)).Status);
        Assert.True(await db.MachineMetrics.AnyAsync(m => m.AgentId == bystanders.Team.TargetId));
    }

    [Fact]
    public async Task Deleting_the_requesters_account_empties_the_runs_grants_and_jobs_it_asked_for()
    {
        var w = await BuildAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await w.Asker.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).StatusCode);
        await AssertEmptiedAsync(w);
        await using var db = api.Db();
        Assert.True(await db.MachineMetrics.AnyAsync(m => m.AgentId == w.Team.TargetId)); // the target's owner is still here
    }

    [Fact]
    public async Task A_run_emptied_by_a_deletion_is_still_listed_without_its_command_for_the_people_who_remain()
    {
        var w = await BuildAsync();
        (await w.Asker.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).EnsureSuccessStatusCode();
        var run = await RemoteKit.WebRunAsync(w.Team.Owner, w.Team.WorkspaceId, w.Finished);
        Assert.Equal("cancelled", run.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("argv").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("reason").ValueKind);
    }
}
