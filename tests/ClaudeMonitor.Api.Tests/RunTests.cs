using System.Net;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Asking for a remote run (ADR-0005): the four keys, the shape of a request, idempotence and the caps.</summary>
[Collection(ApiGroup.Name)]
public sealed class RunTests(ApiFactory api)
{
    [Fact]
    public async Task A_run_is_refused_while_the_workspace_switch_is_off_and_waits_for_its_owner_once_it_is_on()
    {
        var team = await RemoteKit.TeamAsync(api, enabled: false);
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey());
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, req), HttpStatusCode.Conflict, RemoteErrors.Disabled);

        await RemoteKit.SetSwitchAsync(team.Admin, true);
        var created = await RemoteKit.CreateAsync(team.Requester, req);
        Assert.Equal(RunStatuses.PendingApproval, created.Status);
        Assert.Null(created.GrantId);
        Assert.True(created.ExpiresAt > api.Clock.GetUtcNow());
        Assert.True(created.ExpiresAt <= api.Clock.GetUtcNow().AddMinutes(15));
    }

    [Fact]
    public async Task A_target_at_exec_level_off_cannot_run_anything()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Off);
        var response = await RemoteKit.PostRunAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()));
        await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Conflict, RemoteErrors.TargetCannotRun);
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, RemoteKit.Shell(team.TargetId, RemoteKit.NewKey())),
            HttpStatusCode.Conflict, RemoteErrors.TargetCannotRun);
    }

    [Fact]
    public async Task A_target_whose_exec_level_drops_to_off_after_its_profile_cannot_run_any_more()
    {
        var team = await RemoteKit.TeamAsync(api);
        Assert.Equal(RunStatuses.PendingApproval, (await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()))).Status);
        await RemoteKit.ProfileAsync(team.Target, ExecLevels.Off);
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey())),
            HttpStatusCode.Conflict, RemoteErrors.TargetCannotRun);
    }

    [Fact]
    public async Task A_target_that_never_reported_a_profile_is_at_level_off()
    {
        var admin = await api.NewClient().SignedUpAsync("run-old");
        await RemoteKit.SetSwitchAsync(admin, true);
        var requester = await admin.ConnectAgentAsync();
        var oldTarget = await admin.ConnectAgentAsync();
        await RemoteKit.AssertProblemAsync(
            await RemoteKit.PostRunAsync(requester, RemoteKit.Argv(oldTarget.Tokens.AgentId, RemoteKit.NewKey())),
            HttpStatusCode.Conflict, RemoteErrors.TargetCannotRun);
    }

    [Fact]
    public async Task A_shell_run_needs_a_shell_level_and_an_argv_run_is_fine_at_both()
    {
        var argvTeam = await RemoteKit.TeamAsync(api, ExecLevels.Argv);
        await RemoteKit.AssertProblemAsync(
            await RemoteKit.PostRunAsync(argvTeam.Requester, RemoteKit.Shell(argvTeam.TargetId, RemoteKit.NewKey())),
            HttpStatusCode.Conflict, RemoteErrors.TargetCannotRun);
        Assert.Equal(RunStatuses.PendingApproval,
            (await RemoteKit.CreateAsync(argvTeam.Requester, RemoteKit.Argv(argvTeam.TargetId, RemoteKit.NewKey()))).Status);

        var shellTeam = await RemoteKit.TeamAsync(api, ExecLevels.Shell);
        var shell = await RemoteKit.CreateAsync(shellTeam.Requester, RemoteKit.Shell(shellTeam.TargetId, RemoteKit.NewKey()));
        Assert.Equal(RunStatuses.PendingApproval, shell.Status);
        Assert.Equal(RunStatuses.PendingApproval,
            (await RemoteKit.CreateAsync(shellTeam.Requester, RemoteKit.Argv(shellTeam.TargetId, RemoteKit.NewKey()))).Status);
    }

    [Fact]
    public async Task A_target_that_is_unknown_revoked_or_in_another_workspace_is_not_found()
    {
        var team = await RemoteKit.TeamAsync(api);
        var stranger = await api.NewClient().SignedUpAsync("run-stranger");
        var elsewhere = await stranger.ConnectAgentAsync();
        await RemoteKit.ProfileAsync(elsewhere, ExecLevels.Argv);
        var revoked = await team.Owner.ConnectAgentAsync(team.WorkspaceId);
        await RemoteKit.ProfileAsync(revoked, ExecLevels.Argv);
        (await team.Owner.PostAsync($"/api/agents/{revoked.Tokens.AgentId}/revoke")).EnsureSuccessStatusCode();

        foreach (var target in new[] { Guid.NewGuid(), elsewhere.Tokens.AgentId, revoked.Tokens.AgentId })
        {
            var response = await RemoteKit.PostRunAsync(team.Requester, RemoteKit.Argv(target, RemoteKit.NewKey()));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_viewer_cannot_ask_and_a_target_whose_owner_is_only_a_viewer_cannot_run()
    {
        var team = await RemoteKit.TeamAsync(api);
        var asker = await RemoteKit.MemberAsync(api, team.Admin, "run-viewer");
        var askerAgent = await asker.ConnectAgentAsync(team.WorkspaceId);
        await DemoteAsync(team, asker);
        Assert.Equal(HttpStatusCode.NotFound,
            (await RemoteKit.PostRunAsync(askerAgent, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()))).StatusCode);

        await DemoteAsync(team, team.Owner);
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey())),
            HttpStatusCode.Conflict, RemoteErrors.TargetCannotRun);
    }

    private static async Task DemoteAsync(RemoteTeam team, TestUser user) =>
        (await team.Admin.SendAsync(HttpMethod.Patch, $"/api/workspaces/{team.WorkspaceId}/members/{user.Id}", new { role = "viewer" }))
            .EnsureSuccessStatusCode();

    private static string Long(int n) => new('a', n);

    [Fact]
    public async Task A_request_of_the_wrong_shape_is_refused_with_its_code_and_nothing_is_stored()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Shell);
        var t = team.TargetId;
        var ok = RemoteKit.Argv(t, "k");
        var cases = new (string Name, RunCreate Req, string Code)[]
        {
            ("empty key", ok with { ClientKey = "" }, RunRulesCodes.ClientKey),
            ("key of 101", ok with { ClientKey = Long(101) }, RunRulesCodes.ClientKey),
            ("key with a control character", ok with { ClientKey = "a\nb" }, RunRulesCodes.ClientKey),
            ("unknown mode", ok with { Mode = "powershell" }, RunRulesCodes.Mode),
            ("argv and shell together", ok with { ShellCommand = "ls" }, RunRulesCodes.Command),
            ("shell with an argv", RemoteKit.Shell(t, "k") with { Argv = ["/bin/ls"] }, RunRulesCodes.Command),
            ("no argv", ok with { Argv = null }, RunRulesCodes.Command),
            ("empty argv", ok with { Argv = [] }, RunRulesCodes.Command),
            ("relative program", ok with { Argv = ["tail", "-n"] }, RunRulesCodes.Command),
            ("windows program on a mac", ok with { Argv = ["C:\\x.exe"] }, RunRulesCodes.Command),
            ("NUL in an argument", ok with { Argv = ["/bin/ls", "a\0b"] }, RunRulesCodes.Command),
            ("65 elements", ok with { Argv = ["/bin/ls", .. Enumerable.Repeat("x", 64)] }, RunRulesCodes.Command),
            ("argument of 8193", ok with { Argv = ["/bin/ls", Long(8193)] }, RunRulesCodes.Command),
            ("empty shell command", RemoteKit.Shell(t, "k", ""), RunRulesCodes.Command),
            ("shell of 8193", RemoteKit.Shell(t, "k", Long(8193)), RunRulesCodes.Command),
            ("NUL in a shell command", RemoteKit.Shell(t, "k", "ls\0"), RunRulesCodes.Command),
            ("relative cwd", ok with { Cwd = "tmp" }, RunRulesCodes.Cwd),
            ("cwd with a control character", ok with { Cwd = "/tmp/\u0007" }, RunRulesCodes.Cwd),
            ("cwd of 1025", ok with { Cwd = "/" + Long(1024) }, RunRulesCodes.Cwd),
            ("shell with a cwd", RemoteKit.Shell(t, "k") with { Cwd = "/tmp" }, RunRulesCodes.Cwd),
            ("timeout 0", ok with { TimeoutSeconds = 0 }, RunRulesCodes.Timeout),
            ("timeout 3601", ok with { TimeoutSeconds = 3601 }, RunRulesCodes.Timeout),
            ("reason of 501", ok with { Reason = Long(501) }, RunRulesCodes.Reason),
            ("NUL in the reason", ok with { Reason = "a\0" }, RunRulesCodes.Reason),
        };
        var wrong = new List<string>();
        foreach (var (name, req, code) in cases)
        {
            var response = await RemoteKit.PostRunAsync(team.Requester, req);
            var title = response.StatusCode == HttpStatusCode.BadRequest ? await RemoteKit.TitleAsync(response) : null;
            if (response.StatusCode != HttpStatusCode.BadRequest || title != code) wrong.Add($"{name}: {(int)response.StatusCode} {title}");
        }

        Assert.Empty(wrong);
        await using var db = api.Db();
        Assert.False(await db.RemoteRuns.AnyAsync(r => r.RequesterAgentId == team.RequesterId));
    }

    [Fact]
    public async Task The_largest_allowed_request_is_accepted()
    {
        var team = await RemoteKit.TeamAsync(api);
        var biggest = RemoteKit.Argv(team.TargetId, Long(100), ["/bin/ls", .. Enumerable.Repeat(Long(8192), 63)], "/tmp", 3600, Long(500));
        Assert.Equal(RunStatuses.PendingApproval, (await RemoteKit.CreateAsync(team.Requester, biggest)).Status);
        var smallest = RemoteKit.Argv(team.TargetId, "k", ["/"], null, 1);
        Assert.Equal(RunStatuses.PendingApproval, (await RemoteKit.CreateAsync(team.Requester, smallest)).Status);
    }

    [Fact]
    public async Task Asking_again_with_the_same_key_and_command_returns_the_same_run_and_another_command_is_a_conflict()
    {
        var team = await RemoteKit.TeamAsync(api);
        var second = await team.Owner.ConnectAgentAsync(team.WorkspaceId);
        await RemoteKit.ProfileAsync(second, ExecLevels.Argv);
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), cwd: "/tmp", reason: "why");
        var first = await RemoteKit.CreateAsync(team.Requester, req);
        Assert.Equal(first.Id, (await RemoteKit.CreateAsync(team.Requester, req)).Id);
        Assert.Equal(first.Id, (await RemoteKit.CreateAsync(team.Requester, req with { Reason = "another reason" })).Id);

        var changes = new[]
        {
            req with { Argv = [RemoteKit.Tail, "-n", "6", "/var/log/app/api.log"] },
            req with { Argv = [RemoteKit.Tail] },
            req with { TimeoutSeconds = 31 },
            req with { Cwd = "/var" },
            req with { Cwd = null },
            req with { TargetAgentId = second.Tokens.AgentId },
            req with { Mode = RunModes.Shell, Argv = null, ShellCommand = "ls", Cwd = null },
        };
        foreach (var changed in changes)
        {
            await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, changed), HttpStatusCode.Conflict, RemoteErrors.Mismatch);
        }

        await using var db = api.Db();
        Assert.Equal(1, await db.RemoteRuns.CountAsync(r => r.RequesterAgentId == team.RequesterId));
    }

    [Fact]
    public async Task A_key_belongs_to_its_requester_agent_so_another_agent_may_use_the_same_one()
    {
        var team = await RemoteKit.TeamAsync(api);
        var other = await team.Other.ConnectAgentAsync(team.WorkspaceId);
        var req = RemoteKit.Argv(team.TargetId, "shared-key");
        var mine = await RemoteKit.CreateAsync(team.Requester, req);
        var theirs = await RemoteKit.CreateAsync(other, req);
        Assert.NotEqual(mine.Id, theirs.Id);
    }

    [Fact]
    public async Task At_most_ten_runs_wait_for_one_target_and_another_target_is_not_affected()
    {
        var team = await RemoteKit.TeamAsync(api);
        var spare = await team.Owner.ConnectAgentAsync(team.WorkspaceId);
        await RemoteKit.ProfileAsync(spare, ExecLevels.Argv);
        var ids = new List<Guid>();
        for (var i = 0; i < 10; i++) ids.Add((await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()))).Id);

        var eleventh = await RemoteKit.PostRunAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()));
        await RemoteKit.AssertProblemAsync(eleventh, HttpStatusCode.Conflict, RemoteErrors.TargetBusy);
        Assert.Equal(RunStatuses.PendingApproval,
            (await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(spare.Tokens.AgentId, RemoteKit.NewKey()))).Status);

        (await team.Owner.PostAsync($"/api/runs/{ids[0]}/deny", new { reason = "no" })).EnsureSuccessStatusCode();
        Assert.Equal(RunStatuses.PendingApproval,
            (await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()))).Status);
    }

    [Fact]
    public async Task A_requester_may_create_twenty_runs_a_minute_and_the_twenty_first_waits_for_the_minute_to_pass()
    {
        var team = await RemoteKit.TeamAsync(api);
        var spare = await team.Owner.ConnectAgentAsync(team.WorkspaceId);
        await RemoteKit.ProfileAsync(spare, ExecLevels.Argv);
        var first = Guid.Empty;
        for (var i = 0; i < 20; i++)
        {
            var target = i < 10 ? team.TargetId : spare.Tokens.AgentId;
            var created = await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(target, RemoteKit.NewKey()));
            if (i == 0) first = created.Id;
        }

        var limited = await RemoteKit.PostRunAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var third = await team.Owner.ConnectAgentAsync(team.WorkspaceId);
        await RemoteKit.ProfileAsync(third, ExecLevels.Argv);
        var otherRequester = await team.Admin.ConnectAgentAsync();
        Assert.Equal(RunStatuses.PendingApproval,
            (await RemoteKit.CreateAsync(otherRequester, RemoteKit.Argv(third.Tokens.AgentId, RemoteKit.NewKey()))).Status);

        // The first target is full too, so free a place: only the rate limit may then be what is left.
        (await team.Owner.PostAsync($"/api/runs/{first}/deny", new { })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await RemoteKit.PostRunAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()))).StatusCode);
        api.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.OK, (await RemoteKit.PostRunAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()))).StatusCode);
    }

    [Fact]
    public async Task A_new_run_is_audited_with_its_hash_and_never_with_its_command()
    {
        var team = await RemoteKit.TeamAsync(api);
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), ["/bin/echo", "TOPSECRET-VALUE"]);
        var created = await RemoteKit.CreateAsync(team.Requester, req);
        await using var db = api.Db();
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == "remote.run_created" && a.TargetId == created.Id.ToString());
        Assert.Equal(team.WorkspaceId, audit.WorkspaceId);
        Assert.Equal(team.Admin.Id, audit.ActorUserId);
        Assert.Equal(team.RequesterId, audit.ActorAgentId);
        Assert.Equal(RemoteKit.HashOf(req), audit.Detail!.RootElement.GetProperty("hash").GetString());
        Assert.DoesNotContain("TOPSECRET-VALUE", audit.Detail.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    private static class RunRulesCodes
    {
        public const string ClientKey = "invalid_client_key";
        public const string Mode = "invalid_mode";
        public const string Command = "invalid_command";
        public const string Cwd = "invalid_cwd";
        public const string Timeout = "invalid_timeout";
        public const string Reason = "invalid_reason";
    }

}
