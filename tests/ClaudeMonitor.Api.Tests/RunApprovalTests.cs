using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The target owner's answer to a waiting run (ADR-0005, "The owner's approval"), and what the streams hear.</summary>
[Collection(ApiGroup.Name)]
public sealed class RunApprovalTests(ApiFactory api)
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(5);

    private static async Task<(RunCreate Req, Guid Id)> AskAsync(RemoteTeam team, RunCreate? req = null)
    {
        var made = req ?? RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), cwd: "/tmp", reason: "tests");
        return (made, (await RemoteKit.CreateAsync(team.Requester, made)).Id);
    }

    [Fact]
    public async Task Only_the_target_agents_owner_can_approve_and_the_run_then_belongs_to_the_target()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (req, id) = await AskAsync(team);
        foreach (var notOwner in new[] { team.Other, team.Admin })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await RemoteKit.ApproveAsync(notOwner, id, req)).StatusCode);
        }

        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, id));
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(team.Owner, id, req)).StatusCode);
        var row = await RemoteKit.RunRowAsync(api, id);
        Assert.Equal(RunStatuses.Approved, row.Status);
        Assert.Equal(team.Owner.Id, row.DecidedBy);
        Assert.NotNull(row.DecidedAt);
        Assert.Null(row.GrantId);
        Assert.InRange(row.ExpiresAt - api.Clock.GetUtcNow(), TimeSpan.FromMinutes(15) - Ms, TimeSpan.FromMinutes(15) + Ms);
        await RemoteKit.AssertProblemAsync(await RemoteKit.ApproveAsync(team.Owner, id, req), HttpStatusCode.Conflict, RemoteErrors.NotPending);
        await using var db = api.Db();
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == "remote.run_approved" && a.TargetId == id.ToString());
        Assert.Equal(team.Owner.Id, audit.ActorUserId);
        Assert.Equal(RemoteKit.HashOf(req), audit.Detail!.RootElement.GetProperty("hash").GetString());
    }

    [Fact]
    public async Task An_approval_with_a_missing_or_wrong_hash_is_a_conflict_and_changes_nothing()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (req, id) = await AskAsync(team);
        var others = new[]
        {
            RemoteKit.HashOf(req with { TimeoutSeconds = 31 }),
            RemoteKit.HashOf(req with { Cwd = "/var" }),
            RemoteKit.HashOf(req with { Argv = [RemoteKit.Tail] }),
            RemoteKit.HashOf(req with { TargetAgentId = Guid.NewGuid() }),
            RemoteKit.HashOf(req).ToUpperInvariant(),
            "",
        };
        foreach (var hash in others)
        {
            var response = await team.Owner.PostAsync($"/api/runs/{id}/approve", new { hash });
            await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Conflict, RemoteErrors.Mismatch);
        }

        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync($"/api/runs/{id}/approve", new { }), HttpStatusCode.Conflict, RemoteErrors.Mismatch);
        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, id));
    }

    [Fact]
    public async Task The_web_shows_the_owner_the_hash_the_approval_must_carry()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (req, id) = await AskAsync(team);
        var owner = await RemoteKit.WebRunAsync(team.Owner, team.WorkspaceId, id);
        Assert.Equal(RemoteKit.HashOf(req), owner.GetProperty("hash").GetString());
        Assert.True(owner.GetProperty("canDecide").GetBoolean());
        Assert.False(owner.GetProperty("selfApproval").GetBoolean());
        var requester = await RemoteKit.WebRunAsync(team.Admin, team.WorkspaceId, id);
        Assert.Equal(JsonValueKind.Null, requester.GetProperty("hash").ValueKind);
        Assert.False(requester.GetProperty("canDecide").GetBoolean());
        var hash = owner.GetProperty("hash").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/runs/{id}/approve", new { hash })).StatusCode);
    }

    [Fact]
    public async Task A_run_nobody_answered_in_fifteen_minutes_cannot_be_approved_and_the_housekeeper_expires_it()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (req, id) = await AskAsync(team);
        using var requesterStream = await RemoteKit.OpenAsync(team.Requester.Http, "/api/agent/stream");
        await RemoteKit.NextEventAsync(requesterStream, AgentStreamEvents.Ready);

        api.Clock.Advance(TimeSpan.FromMinutes(16));
        await RemoteKit.AssertProblemAsync(await RemoteKit.ApproveAsync(team.Owner, id, req), HttpStatusCode.Conflict, RemoteErrors.NotPending);
        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, id));

        await RemoteKit.HousekeepAsync(api);

        var update = await RemoteKit.RunEventAsync(requesterStream, AgentStreamEvents.RunUpdate, id);
        Assert.Equal(RunStatuses.Expired, update.GetProperty("status").GetString());
        var row = await RemoteKit.RunRowAsync(api, id);
        Assert.Equal(RunStatuses.Expired, row.Status);
        Assert.NotNull(row.EndedAt);
        await RemoteKit.AssertProblemAsync(await RemoteKit.ApproveAsync(team.Owner, id, req), HttpStatusCode.Conflict, RemoteErrors.NotPending);
    }

    [Fact]
    public async Task An_approved_run_the_target_never_started_expires_and_the_target_is_told_to_drop_it()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (req, id) = await AskAsync(team);
        using var targetStream = await RemoteKit.OpenAsync(team.Target.Http, "/api/agent/stream");
        await RemoteKit.NextEventAsync(targetStream, AgentStreamEvents.Ready);
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(team.Owner, id, req)).StatusCode);
        await RemoteKit.RunEventAsync(targetStream, AgentStreamEvents.Run, id);

        api.Clock.Advance(TimeSpan.FromMinutes(16));
        await RemoteKit.HousekeepAsync(api);

        await RemoteKit.RunEventAsync(targetStream, AgentStreamEvents.RunCancel, id);
        Assert.Equal(RunStatuses.Expired, await RemoteKit.StatusOfAsync(api, id));
    }

    [Fact]
    public async Task Denying_is_the_owners_alone_ends_the_run_and_tells_the_requester()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (req, id) = await AskAsync(team);
        using var requesterStream = await RemoteKit.OpenAsync(team.Requester.Http, "/api/agent/stream");
        await RemoteKit.NextEventAsync(requesterStream, AgentStreamEvents.Ready);
        Assert.Equal(HttpStatusCode.NotFound, (await team.Other.PostAsync($"/api/runs/{id}/deny", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await team.Admin.PostAsync($"/api/runs/{id}/deny", new { reason = "x" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/runs/{id}/deny", new { reason = new string('r', 700) })).StatusCode);
        var update = await RemoteKit.RunEventAsync(requesterStream, AgentStreamEvents.RunUpdate, id);
        Assert.Equal(RunStatuses.Denied, update.GetProperty("status").GetString());
        var row = await RemoteKit.RunRowAsync(api, id);
        Assert.Equal((RunStatuses.Denied, team.Owner.Id), (row.Status, row.DecidedBy));
        Assert.Equal(500, row.Error!.Length);
        Assert.NotNull(row.EndedAt);
        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync($"/api/runs/{id}/deny", new { }), HttpStatusCode.Conflict, RemoteErrors.NotPending);
        await RemoteKit.AssertProblemAsync(await RemoteKit.ApproveAsync(team.Owner, id, req), HttpStatusCode.Conflict, RemoteErrors.NotPending);
        var seen = await team.Requester.Http.GetFromJsonAsync<RunView>("/api/agent/runs/" + id, TestUser.Json);
        Assert.Equal(RunStatuses.Denied, seen!.Status);
    }

    [Fact]
    public async Task A_shell_run_asks_an_owner_with_two_step_sign_in_for_a_valid_code_and_other_owners_just_click()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Shell);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        var req = RemoteKit.Shell(team.TargetId, RemoteKit.NewKey());
        var id = (await RemoteKit.CreateAsync(team.Requester, req)).Id;

        await RemoteKit.AssertProblemAsync(await RemoteKit.ApproveAsync(team.Owner, id, req), HttpStatusCode.Forbidden, RemoteErrors.MfaRequired);
        await RemoteKit.AssertProblemAsync(await RemoteKit.ApproveAsync(team.Owner, id, req, "000000"), HttpStatusCode.Forbidden, RemoteErrors.MfaRequired);
        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, id));
        var valid = await RemoteKit.ApproveAsync(team.Owner, id, req, RemoteKit.TotpNow(api, secret));
        Assert.Equal(HttpStatusCode.NoContent, valid.StatusCode);
        Assert.Equal(RunStatuses.Approved, await RemoteKit.StatusOfAsync(api, id));

        var plain = await RemoteKit.TeamAsync(api, ExecLevels.Shell);
        var plainReq = RemoteKit.Shell(plain.TargetId, RemoteKit.NewKey());
        var plainId = (await RemoteKit.CreateAsync(plain.Requester, plainReq)).Id;
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(plain.Owner, plainId, plainReq)).StatusCode);
    }

    [Fact]
    public async Task An_argv_run_never_asks_for_a_code_even_when_the_owner_has_two_step_sign_in()
    {
        var team = await RemoteKit.TeamAsync(api);
        await RemoteKit.EnableTotpAsync(api, team.Owner);
        var (req, id) = await AskAsync(team);
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(team.Owner, id, req)).StatusCode);
    }

    [Fact]
    public async Task A_requester_who_is_also_the_owner_still_has_to_click_allow()
    {
        var team = await RemoteKit.TeamAsync(api);
        var mine = await team.Admin.ConnectAgentAsync();
        await RemoteKit.ProfileAsync(mine, ExecLevels.Argv);
        var req = RemoteKit.Argv(mine.Tokens.AgentId, RemoteKit.NewKey());
        var created = await RemoteKit.CreateAsync(team.Requester, req);
        Assert.Equal(RunStatuses.PendingApproval, created.Status);
        Assert.Null(created.GrantId);
        var view = await RemoteKit.WebRunAsync(team.Admin, team.WorkspaceId, created.Id);
        Assert.True(view.GetProperty("selfApproval").GetBoolean());
        Assert.True(view.GetProperty("canDecide").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(team.Admin, created.Id, req)).StatusCode);
    }

    [Fact]
    public async Task An_approval_after_the_target_lowered_its_exec_level_is_refused_and_the_run_stays_waiting()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (req, id) = await AskAsync(team);
        await RemoteKit.ProfileAsync(team.Target, ExecLevels.Off);
        await RemoteKit.AssertProblemAsync(await RemoteKit.ApproveAsync(team.Owner, id, req), HttpStatusCode.Conflict, RemoteErrors.TargetCannotRun);
        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, id));
        await RemoteKit.ProfileAsync(team.Target, ExecLevels.Argv);
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(team.Owner, id, req)).StatusCode);
    }

    [Fact]
    public async Task An_approved_run_reaches_the_targets_open_stream_with_its_command_and_no_grant()
    {
        var team = await RemoteKit.TeamAsync(api);
        using var stream = await RemoteKit.OpenAsync(team.Target.Http, "/api/agent/stream");
        await RemoteKit.NextEventAsync(stream, AgentStreamEvents.Ready);
        var (req, id) = await AskAsync(team);
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(team.Owner, id, req)).StatusCode);

        var run = await RemoteKit.RunEventAsync(stream, AgentStreamEvents.Run, id);
        Assert.Equal("argv", run.GetProperty("mode").GetString());
        Assert.Equal(req.Argv, run.GetProperty("argv").EnumerateArray().Select(a => a.GetString()!).ToList());
        Assert.Equal("/tmp", run.GetProperty("cwd").GetString());
        Assert.Equal(30, run.GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("grantId").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("grant").ValueKind);
        Assert.InRange(run.GetProperty("notAfter").GetDateTimeOffset() - api.Clock.GetUtcNow(), TimeSpan.FromMinutes(15) - Ms, TimeSpan.FromMinutes(15) + Ms);
    }

    [Fact]
    public async Task A_run_approved_while_the_target_was_away_is_replayed_on_reconnect_and_a_finished_one_is_not()
    {
        var team = await RemoteKit.TeamAsync(api);
        var (reqDone, done) = await AskAsync(team);
        api.Clock.Advance(TimeSpan.FromSeconds(1)); // the stream replays oldest first, so a finished run would come first
        var (reqWaiting, waiting) = await AskAsync(team);
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(team.Owner, done, reqDone)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.StatusAsync(team.Target, done, new RunStatusUpdate("running"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.StatusAsync(team.Target, done, new RunStatusUpdate("succeeded", 0))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(team.Owner, waiting, reqWaiting)).StatusCode);

        using var stream = await RemoteKit.OpenAsync(team.Target.Http, "/api/agent/stream");
        var first = await RemoteKit.NextEventAsync(stream, AgentStreamEvents.Run);
        Assert.Equal(waiting, first.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_requesters_stream_hears_every_change_of_its_run()
    {
        var team = await RemoteKit.TeamAsync(api);
        using var stream = await RemoteKit.OpenAsync(team.Requester.Http, "/api/agent/stream");
        await RemoteKit.NextEventAsync(stream, AgentStreamEvents.Ready);
        var (req, id) = await AskAsync(team);
        Assert.Equal(RunStatuses.PendingApproval, (await RemoteKit.RunEventAsync(stream, AgentStreamEvents.RunUpdate, id)).GetProperty("status").GetString());
        (await RemoteKit.ApproveAsync(team.Owner, id, req)).EnsureSuccessStatusCode();
        Assert.Equal(RunStatuses.Approved, (await RemoteKit.RunEventAsync(stream, AgentStreamEvents.RunUpdate, id)).GetProperty("status").GetString());
        (await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate("running"))).EnsureSuccessStatusCode();
        Assert.Equal(RunStatuses.Running, (await RemoteKit.RunEventAsync(stream, AgentStreamEvents.RunUpdate, id)).GetProperty("status").GetString());
    }
}
