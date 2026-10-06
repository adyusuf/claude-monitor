using System.Net;
using System.Linq.Expressions;
using System.Net.Http.Json;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>
/// Run creation under real concurrency (ADR-0005): requests that start together and interleave inside the API and
/// PostgreSQL. The invariants are the ones the creator's key constraint and row locks exist for.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class RunConcurrencyTests(ApiFactory api)
{
    private const int Requests = 8;
    private const int Rounds = 6;
    private static readonly string[] Open = [RunStatuses.PendingApproval, RunStatuses.Approved, RunStatuses.Delivered, RunStatuses.Running];
    private static readonly string[] Suite = ["/opt/ci/run-tests", "--suite", "unit"];

    /// <summary>What ends a team's remote work while runs are being asked for (each is meant to be serialised against creation).</summary>
    public enum Disruption
    {
        SwitchOff,
        TargetRevoked,
        TargetOwnerRemoved,
    }

    /// <summary>Starts every action at the same moment and returns what each did; nothing begins before all are ready.</summary>
    private static async Task<T[]> TogetherAsync<T>(IEnumerable<Func<Task<T>>> actions)
    {
        var start = new TaskCompletionSource();
        var running = actions.Select(a => Task.Run(async () =>
        {
            await start.Task;
            return await a();
        })).ToArray();
        start.SetResult();
        return await Task.WhenAll(running);
    }

    private static Task<HttpResponseMessage> Disrupt(RemoteTeam team, Disruption how) => how switch
    {
        Disruption.SwitchOff => team.Admin.SendAsync(HttpMethod.Put, $"/api/workspaces/{team.WorkspaceId}/remote-settings", new { remoteRunsEnabled = false }),
        Disruption.TargetRevoked => team.Admin.PostAsync($"/api/agents/{team.TargetId}/revoke"),
        Disruption.TargetOwnerRemoved => team.Admin.SendAsync(HttpMethod.Delete, $"/api/workspaces/{team.WorkspaceId}/members/{team.Owner.Id}"),
        _ => throw new ArgumentOutOfRangeException(nameof(how), how, null),
    };

    private async Task<List<RemoteRun>> RunsAsync(Expression<Func<RemoteRun, bool>> where)
    {
        await using var db = api.Db();
        return await db.RemoteRuns.AsNoTracking().Where(where).ToListAsync();
    }

    [Fact]
    public async Task Many_requests_with_one_client_key_create_one_run_and_every_answer_names_it()
    {
        var team = await RemoteKit.TeamAsync(api);
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey());

        var answers = await TogetherAsync(Enumerable.Range(0, Requests).Select<int, Func<Task<HttpResponseMessage>>>(_ => () => RemoteKit.PostRunAsync(team.Requester, req)));

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.OK, a.StatusCode));
        var created = await Task.WhenAll(answers.Select(a => a.Content.ReadFromJsonAsync<RunCreated>(TestUser.Json)));
        var ids = created.Select(c => c!.Id).Distinct().ToList();
        var rows = await RunsAsync(r => r.RequesterAgentId == team.RequesterId);
        Assert.Equal(ids, rows.Select(r => r.Id));
        Assert.Single(ids);
        Assert.All(created, c => Assert.Equal(RunStatuses.PendingApproval, c!.Status));
    }

    [Fact]
    public async Task Concurrent_requests_with_different_keys_each_create_their_own_run()
    {
        var team = await RemoteKit.TeamAsync(api);

        var answers = await TogetherAsync(Enumerable.Range(0, Requests).Select<int, Func<Task<HttpResponseMessage>>>(
            _ => () => RemoteKit.PostRunAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()))));

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.OK, a.StatusCode));
        var created = await Task.WhenAll(answers.Select(a => a.Content.ReadFromJsonAsync<RunCreated>(TestUser.Json)));
        Assert.Equal(Requests, created.Select(c => c!.Id).Distinct().Count());
        Assert.Equal(Requests, (await RunsAsync(r => r.RequesterAgentId == team.RequesterId)).Count);
    }

    [Theory]
    [InlineData(Disruption.SwitchOff)]
    [InlineData(Disruption.TargetRevoked)]
    [InlineData(Disruption.TargetOwnerRemoved)]
    public async Task A_run_asked_for_while_remote_work_is_being_ended_never_outlives_it(Disruption how)
    {
        for (var round = 0; round < Rounds; round++)
        {
            var team = await RemoteKit.TeamAsync(api);
            var actions = Enumerable.Range(0, Requests).Select<int, Func<Task<HttpResponseMessage>>>(
                _ => () => RemoteKit.PostRunAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()))).ToList();
            actions.Insert(Requests / 2, () => Disrupt(team, how));

            var answers = await TogetherAsync(actions);

            // Whatever interleaving happened, nothing fails and nothing is left open once the ending has committed.
            var disruption = answers[Requests / 2];
            Assert.Contains(disruption.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.NoContent });
            Assert.All(answers, a => Assert.True((int)a.StatusCode < 500, $"{how}: {(int)a.StatusCode}"));
            Assert.All(answers.Where((_, i) => i != Requests / 2),
                a => Assert.Contains(a.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.Conflict }));
            var open = await RunsAsync(r => r.RequesterAgentId == team.RequesterId && Open.Contains(r.Status));
            Assert.Empty(open);
        }
    }

    /// <summary>
    /// The owner retires a job at the moment a run it approved is about to commit: the run's insert is held (a lock on the
    /// requester's user row, which its foreign key needs) while the retire is sent. The job row lock makes the retire wait for
    /// the run, so its sweep finds the run; without it the retire finishes first and the run is approved by a retired job.
    /// </summary>
    [Fact]
    public async Task A_run_the_job_approved_just_before_it_is_retired_is_swept_by_the_retire()
    {
        var team = await RemoteKit.TeamAsync(api);
        var job = await ActiveJobAsync(team);
        await using var holder = api.Db();
        await using var hold = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {team.Admin.Id} FOR UPDATE");

        var create = Task.Run(() => RemoteKit.PostRunAsync(team.Requester, JobRun(team.TargetId, job.Id)));
        await WaitUntilAsync(async () => await WaitingBackendsAsync() >= 1);
        var retire = Task.Run(() => team.Owner.PostAsync($"/api/jobs/{job.Id}/retire"));
        await WaitUntilAsync(async () => retire.IsCompleted || await WaitingBackendsAsync() >= 2);
        await hold.CommitAsync();

        Assert.Equal(HttpStatusCode.OK, (await create).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await retire).StatusCode);
        var runs = await RunsAsync(r => r.RequesterAgentId == team.RequesterId);
        Assert.Equal(RunStatuses.Cancelled, Assert.Single(runs).Status);
        Assert.Equal(job.Id, runs[0].JobId);
    }

    /// <summary>
    /// The requester is removed from the workspace while their run is about to commit (its insert is held by a lock on the target
    /// owner's user row, which its foreign key needs). The members lock makes the removal wait for the run, so its sweep cancels it;
    /// without it the removal finishes first and a run of a removed member stays open.
    /// </summary>
    [Fact]
    public async Task A_run_about_to_commit_when_its_requester_is_removed_is_cancelled_by_the_removal()
    {
        var team = await RemoteKit.TeamAsync(api);
        var asker = await RemoteKit.MemberAsync(api, team.Admin, "rc-asker");
        var askerAgent = await asker.ConnectAgentAsync(team.WorkspaceId);
        await using var holder = api.Db();
        await using var hold = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {team.Owner.Id} FOR UPDATE");

        var create = Task.Run(() => RemoteKit.PostRunAsync(askerAgent, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey())));
        await WaitUntilAsync(async () => await WaitingBackendsAsync() >= 1);
        var remove = Task.Run(() => team.Admin.SendAsync(HttpMethod.Delete, $"/api/workspaces/{team.WorkspaceId}/members/{asker.Id}"));
        await WaitUntilAsync(async () => remove.IsCompleted || await WaitingBackendsAsync() >= 2);
        await hold.CommitAsync();

        Assert.Equal(HttpStatusCode.OK, (await create).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await remove).StatusCode);
        var run = Assert.Single(await RunsAsync(r => r.RequesterAgentId == askerAgent.Tokens.AgentId));
        Assert.Equal(RunStatuses.Cancelled, run.Status);
    }

    [Fact]
    public async Task Concurrent_runs_of_an_active_job_are_all_approved_by_it_and_none_is_lost()
    {
        var team = await RemoteKit.TeamAsync(api);
        var job = await ActiveJobAsync(team);

        var answers = await TogetherAsync(Enumerable.Range(0, Requests).Select<int, Func<Task<HttpResponseMessage>>>(
            _ => () => RemoteKit.PostRunAsync(team.Requester, JobRun(team.TargetId, job.Id))));

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.OK, a.StatusCode));
        var rows = await RunsAsync(r => r.RequesterAgentId == team.RequesterId);
        Assert.Equal(Requests, rows.Count);
        Assert.All(rows, r => Assert.Equal((RunStatuses.Approved, job.Id, team.Owner.Id), (r.Status, r.JobId, r.DecidedBy)));
    }

    private async Task<int> WaitingBackendsAsync()
    {
        await using var db = api.Db();
        return await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE wait_event_type = 'Lock'").SingleAsync();
    }

    /// <summary>Waits on a condition, never a fixed time; fails after ten seconds.</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition()) await Task.Delay(20, timeout.Token);
    }

    private static RunCreate JobRun(Guid target, Guid job) => RemoteKit.Argv(target, RemoteKit.NewKey(), Suite, "/opt/ci", 120) with { JobId = job };

    private static async Task<JobView> ActiveJobAsync(RemoteTeam team)
    {
        var proposal = new JobProposal(team.TargetId, "Job " + Guid.NewGuid().ToString("N")[..8], Suite, "/opt/ci", 300, null);
        var response = await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs", proposal, TestUser.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<JobView>(TestUser.Json))!;
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/jobs/{job.Id}/approve", new { })).StatusCode);
        return job;
    }
}
