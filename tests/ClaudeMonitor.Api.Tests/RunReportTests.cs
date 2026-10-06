using System.Net;
using System.Net.Http.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>What the target reports about a run it was sent: the status ladder and the output (ADR-0004).</summary>
[Collection(ApiGroup.Name)]
public sealed class RunReportTests(ApiFactory api)
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(5);

    private async Task<(RemoteTeam Team, Guid Id)> ApprovedAsync(bool running = false)
    {
        var team = await RemoteKit.TeamAsync(api);
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey());
        var id = (await RemoteKit.CreateAsync(team.Requester, req)).Id;
        (await RemoteKit.ApproveAsync(team.Owner, id, req)).EnsureSuccessStatusCode();
        if (running) Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate("running"))).StatusCode);
        return (team, id);
    }

    private static Task<HttpResponseMessage> Chunks(TestAgent agent, Guid id, params RunOutputChunk[] chunks) =>
        agent.Http.PostAsJsonAsync($"/api/agent/runs/{id}/output", chunks, TestUser.Json);

    private async Task<(int Count, long Bytes, bool Truncated)> StoredAsync(Guid id)
    {
        await using var db = api.Db();
        var row = await db.RemoteRuns.AsNoTracking().SingleAsync(r => r.Id == id);
        return (await db.RemoteRunOutput.CountAsync(o => o.RunId == id), row.OutputBytes, row.OutputTruncated);
    }

    [Fact]
    public async Task A_run_moves_from_approved_to_delivered_to_running_and_never_backwards()
    {
        var (team, id) = await ApprovedAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate("delivered"))).StatusCode);
        Assert.NotNull((await RemoteKit.RunRowAsync(api, id)).DeliveredAt);
        Assert.Equal(HttpStatusCode.Conflict, (await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate("delivered"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate("running", ResolvedExe: "/usr/bin/tail"))).StatusCode);
        var row = await RemoteKit.RunRowAsync(api, id);
        Assert.Equal((RunStatuses.Running, "/usr/bin/tail"), (row.Status, row.ResolvedExe));
        foreach (var back in new[] { "delivered", "running" })
        {
            Assert.Equal(HttpStatusCode.Conflict, (await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate(back))).StatusCode);
        }
    }

    [Fact]
    public async Task A_target_may_report_running_without_a_delivered_report_first()
    {
        var (team, id) = await ApprovedAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate("running"))).StatusCode);
        Assert.Equal(RunStatuses.Running, await RemoteKit.StatusOfAsync(api, id));
    }

    [Theory]
    [InlineData("succeeded", 0, null)]
    [InlineData("failed", 3, "exit 3")]
    [InlineData("timed_out", null, "timeout")]
    [InlineData("cancelled", null, null)]
    public async Task A_final_report_ends_the_run_once_and_a_second_or_backwards_report_is_a_conflict(string status, int? exit, string? error)
    {
        var (team, id) = await ApprovedAsync(running: true);
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate(status, exit, error))).StatusCode);
        var row = await RemoteKit.RunRowAsync(api, id);
        Assert.Equal((status, exit, error), (row.Status, row.ExitCode, row.Error));
        Assert.NotNull(row.EndedAt);
        foreach (var next in new[] { status, "running", "delivered", "failed", "succeeded" })
        {
            Assert.Equal(HttpStatusCode.Conflict, (await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate(next, 9, "late"))).StatusCode);
        }

        var after = await RemoteKit.RunRowAsync(api, id);
        Assert.Equal((status, exit, error), (after.Status, after.ExitCode, after.Error));
        await using var db = api.Db();
        Assert.Single(await db.AuditEvents.AsNoTracking().Where(a => a.Action == "remote.run_finished" && a.TargetId == id.ToString()).ToListAsync());
    }

    [Fact]
    public async Task A_final_report_can_finish_a_run_that_was_never_reported_running()
    {
        var (team, id) = await ApprovedAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate("failed", 1, "refused by the target"))).StatusCode);
        Assert.Equal(RunStatuses.Failed, await RemoteKit.StatusOfAsync(api, id));
    }

    [Fact]
    public async Task The_error_text_loses_its_control_characters_is_cut_to_500_and_truncation_sticks()
    {
        var (team, id) = await ApprovedAsync(running: true);
        var error = "boom\u001b[31m\nline" + new string('x', 600);
        var report = new RunStatusUpdate("failed", 2, error, OutputTruncated: true);
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.StatusAsync(team.Target, id, report)).StatusCode);
        var row = await RemoteKit.RunRowAsync(api, id);
        Assert.Equal(("boom[31mline" + new string('x', 600))[..500], row.Error);
        Assert.True(row.OutputTruncated);
    }

    [Fact]
    public async Task Only_the_runs_target_may_report_and_only_statuses_an_agent_can_know()
    {
        var (team, id) = await ApprovedAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await RemoteKit.StatusAsync(team.Requester, id, new RunStatusUpdate("running"))).StatusCode);
        var stranger = await team.Other.ConnectAgentAsync(team.WorkspaceId);
        Assert.Equal(HttpStatusCode.NotFound, (await RemoteKit.StatusAsync(stranger, id, new RunStatusUpdate("failed"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RemoteKit.StatusAsync(team.Target, Guid.NewGuid(), new RunStatusUpdate("running"))).StatusCode);
        foreach (var bad in new[] { "pending_approval", "approved", "denied", "expired", "bogus", "" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate(bad))).StatusCode);
        }

        Assert.Equal(RunStatuses.Approved, await RemoteKit.StatusOfAsync(api, id));
    }

    [Fact]
    public async Task A_run_that_was_never_approved_cannot_be_reported_on_by_its_target()
    {
        var team = await RemoteKit.TeamAsync(api);
        var id = (await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()))).Id;
        foreach (var status in new[] { "delivered", "running", "succeeded", "failed" })
        {
            Assert.Equal(HttpStatusCode.Conflict, (await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate(status, 0))).StatusCode);
        }

        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, id));
    }

    [Fact]
    public async Task A_reported_time_is_believed_only_inside_the_clock_skew_and_never_from_the_future()
    {
        var now = api.Clock.GetUtcNow();
        var cases = new (DateTimeOffset At, TimeSpan Expected)[]
        {
            (now.AddSeconds(-30), TimeSpan.FromSeconds(-30)),
            (now.AddHours(-1), TimeSpan.Zero),
            (now.AddMinutes(5), TimeSpan.Zero),
        };
        foreach (var (at, expected) in cases)
        {
            var (team, id) = await ApprovedAsync();
            await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate("running", At: at));
            var started = (await RemoteKit.RunRowAsync(api, id)).StartedAt!.Value;
            Assert.InRange(started - api.Clock.GetUtcNow(), expected - Ms, expected + Ms);
        }
    }

    [Fact]
    public async Task Output_chunks_are_stored_once_per_sequence_number_and_counted_once()
    {
        var (team, id) = await ApprovedAsync(running: true);
        var first = new[] { new RunOutputChunk(0, "stdout", "hello\n"), new RunOutputChunk(1, "stderr", "warn\n", GapBefore: true) };
        Assert.Equal(HttpStatusCode.NoContent, (await Chunks(team.Target, id, first)).StatusCode);
        Assert.Equal((2, 11L, false), await StoredAsync(id));
        var again = new[] { first[0], new RunOutputChunk(1, "stderr", "DIFFERENT"), new RunOutputChunk(2, "stdout", "more") };
        Assert.Equal(HttpStatusCode.NoContent, (await Chunks(team.Target, id, again)).StatusCode);
        Assert.Equal((3, 15L, false), await StoredAsync(id));

        var page = await team.Requester.Http.GetFromJsonAsync<RunOutputPage>($"/api/agent/runs/{id}/output", TestUser.Json);
        Assert.Equal(["hello\n", "warn\n", "more"], page!.Chunks.Select(c => c.Body).ToArray());
        Assert.Equal(["stdout", "stderr", "stdout"], page.Chunks.Select(c => c.Stream).ToArray());
        Assert.Equal([false, true, false], page.Chunks.Select(c => c.GapBefore).ToArray());
        Assert.Equal(2, page.NextSeq);
        Assert.False(page.Done);
    }

    [Fact]
    public async Task A_run_stores_one_megabyte_of_output_exactly_and_refuses_the_next_byte_marking_it_truncated()
    {
        var (team, id) = await ApprovedAsync(running: true);
        var block = new string('x', 64 * 1024);
        var full = Enumerable.Range(0, 16).Select(i => new RunOutputChunk(i, "stdout", block)).ToArray();
        Assert.Equal(HttpStatusCode.NoContent, (await Chunks(team.Target, id, full)).StatusCode);
        Assert.Equal((16, 1024L * 1024, false), await StoredAsync(id));

        Assert.Equal(HttpStatusCode.NoContent, (await Chunks(team.Target, id, new RunOutputChunk(16, "stdout", "y"))).StatusCode);
        Assert.Equal((16, 1024L * 1024, true), await StoredAsync(id));
        var page = await team.Requester.Http.GetFromJsonAsync<RunOutputPage>($"/api/agent/runs/{id}/output?after=14&limit=50", TestUser.Json);
        Assert.Equal([15], page!.Chunks.Select(c => c.Seq).ToArray());
    }

    [Fact]
    public async Task A_malformed_chunk_list_is_refused_whole_and_stores_nothing()
    {
        var (team, id) = await ApprovedAsync(running: true);
        var ok = new RunOutputChunk(0, "stdout", "fine");
        var bad = new RunOutputChunk[][]
        {
            [],
            [.. Enumerable.Range(0, 17).Select(i => new RunOutputChunk(i, "stdout", "x"))],
            [ok, new RunOutputChunk(1, "stdout", "a\0b")],
            [ok, new RunOutputChunk(1, "stdin", "x")],
            [ok, new RunOutputChunk(-1, "stdout", "x")],
            [ok, new RunOutputChunk(1, "stdout", "")],
            [ok, new RunOutputChunk(1, "stdout", new string('x', 64 * 1024 + 1))],
        };
        foreach (var chunks in bad)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await Chunks(team.Target, id, chunks)).StatusCode);
        }

        Assert.Equal((0, 0L, false), await StoredAsync(id));
    }

    [Fact]
    public async Task Output_is_taken_from_the_runs_target_only_and_only_while_the_run_is_live()
    {
        var team = await RemoteKit.TeamAsync(api);
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey());
        var waiting = (await RemoteKit.CreateAsync(team.Requester, req)).Id;
        await Chunks(team.Target, waiting, new RunOutputChunk(0, "stdout", "too early"));
        var stored = await StoredAsync(waiting);
        Assert.Equal((0, 0L), (stored.Count, stored.Bytes));

        var id = waiting;
        (await RemoteKit.ApproveAsync(team.Owner, id, req)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await Chunks(team.Requester, id, new RunOutputChunk(0, "stdout", "forged"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Chunks(team.Target, Guid.NewGuid(), new RunOutputChunk(0, "stdout", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Chunks(team.Target, id, new RunOutputChunk(0, "stdout", "ok"))).StatusCode);
        (await team.Admin.PostAsync($"/api/runs/{id}/cancel")).EnsureSuccessStatusCode();
        await Chunks(team.Target, id, new RunOutputChunk(1, "stdout", "after the end"));
        var end = await StoredAsync(id);
        Assert.Equal((1, 2L), (end.Count, end.Bytes));
    }
}
