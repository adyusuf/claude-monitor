using System.Net;
using System.Net.Http.Json;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Decision §9.13: on a strict target (production or database) a shell command, an interpreter run, a grant and a job need the owner's two-step code, like a script.</summary>
[Collection(ApiGroup.Name)]
public sealed class StrictCodeTests(ApiFactory api)
{
    private static readonly string[] Interpreter = ["/bin/sh", "-c", "true"];

    private static Task<RemoteTeam> StrictTeamAsync(ApiFactory api, string environment = "production", string function = "application") =>
        ScriptKit.TeamAsync(api, environment, function, ExecLevels.Shell);

    private static async Task<Guid> CreateAsync(RemoteTeam team, RunCreate req)
    {
        var response = await RemoteKit.PostRunAsync(team.Requester, req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RunCreated>(TestUser.Json))!.Id;
    }

    // ---- the rule itself ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RunModes.Script, null, true, true)]
    [InlineData(RunModes.Shell, null, true, true)]
    [InlineData(RunModes.Argv, "/bin/sh", true, true)]
    [InlineData(RunModes.Argv, "/usr/bin/tail", true, false)]
    [InlineData(RunModes.Argv, "/bin/sh", false, false)]
    [InlineData(RunModes.Shell, null, false, false)]
    [InlineData(RunModes.Script, null, false, false)]
    [InlineData("unknown", "/bin/sh", true, false)]
    public void The_rule_asks_for_a_code_for_a_script_a_shell_and_an_interpreter_run_on_a_strict_target_only(string mode, string? argv0, bool strict, bool expected) =>
        Assert.Equal(expected, RunCodeRule.NeedsCode(mode, argv0 is null ? null : [argv0, "x"], OsKinds.Linux, strict));

    [Fact]
    public void An_argv_run_without_a_program_or_an_unknown_os_never_needs_a_code()
    {
        Assert.False(RunCodeRule.NeedsCode(RunModes.Argv, [], OsKinds.Linux, true));
        Assert.False(RunCodeRule.NeedsCode(RunModes.Argv, null, OsKinds.Linux, true));
        Assert.False(RunCodeRule.NeedsCode(RunModes.Argv, ["/bin/sh"], null, true));
    }

    // ---- a shell command ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("production", "application")]
    [InlineData("test", "database")]
    public async Task A_shell_command_on_a_strict_target_cannot_be_approved_by_an_owner_without_two_step_sign_in(string environment, string function)
    {
        var team = await StrictTeamAsync(api, environment, function);
        var req = RemoteKit.Shell(team.TargetId, RemoteKit.NewKey());
        var id = await CreateAsync(team, req);

        await RemoteKit.AssertProblemAsync(await RemoteKit.ApproveAsync(team.Owner, id, req), HttpStatusCode.Forbidden, RemoteErrors.TotpRequired);

        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, id));
    }

    [Fact]
    public async Task A_shell_command_on_a_strict_target_needs_a_valid_code_and_is_then_marked_as_approved_with_one()
    {
        var team = await StrictTeamAsync(api);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        var req = RemoteKit.Shell(team.TargetId, RemoteKit.NewKey());
        var id = await CreateAsync(team, req);

        await RemoteKit.AssertProblemAsync(await RemoteKit.ApproveAsync(team.Owner, id, req), HttpStatusCode.Forbidden, RemoteErrors.MfaRequired);
        await RemoteKit.AssertProblemAsync(await RemoteKit.ApproveAsync(team.Owner, id, req, "000000"), HttpStatusCode.Forbidden, RemoteErrors.MfaRequired);
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(team.Owner, id, req, RemoteKit.TotpNow(api, secret))).StatusCode);

        Assert.True((await RemoteKit.RunRowAsync(api, id)).ApprovedWithTotp);
    }

    [Fact]
    public async Task A_shell_command_on_a_target_that_is_not_strict_is_approved_with_a_click_when_the_owner_has_no_two_step_sign_in()
    {
        var team = await ScriptKit.TeamAsync(api, "test", "application", ExecLevels.Shell);
        var req = RemoteKit.Shell(team.TargetId, RemoteKit.NewKey());
        var id = await CreateAsync(team, req);

        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(team.Owner, id, req)).StatusCode);
        Assert.False((await RemoteKit.RunRowAsync(api, id)).ApprovedWithTotp);
    }

    // ---- an argv run of an interpreter, and any other argv run ---------------------------------------------------

    [Fact]
    public async Task An_interpreter_run_on_a_strict_target_needs_the_code_and_a_plain_argv_run_there_keeps_the_click()
    {
        var team = await StrictTeamAsync(api);
        var interpreter = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), Interpreter);
        var plain = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey());
        var interpreterId = await CreateAsync(team, interpreter);
        var plainId = await CreateAsync(team, plain);

        await RemoteKit.AssertProblemAsync(await RemoteKit.ApproveAsync(team.Owner, interpreterId, interpreter), HttpStatusCode.Forbidden, RemoteErrors.TotpRequired);
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(team.Owner, plainId, plain)).StatusCode);
        Assert.False((await RemoteKit.RunRowAsync(api, plainId)).ApprovedWithTotp);
    }

    [Fact]
    public async Task An_interpreter_run_on_a_strict_target_is_approved_with_a_valid_code()
    {
        var team = await StrictTeamAsync(api);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), Interpreter);
        var id = await CreateAsync(team, req);

        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(team.Owner, id, req, RemoteKit.TotpNow(api, secret))).StatusCode);
        Assert.True((await RemoteKit.RunRowAsync(api, id)).ApprovedWithTotp);
    }

    [Fact]
    public async Task An_interpreter_run_on_a_target_that_is_not_strict_is_still_approved_with_a_click()
    {
        var team = await ScriptKit.TeamAsync(api, "test", "application", ExecLevels.Shell);
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), Interpreter);
        var id = await CreateAsync(team, req);
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(team.Owner, id, req)).StatusCode);
    }

    // ---- what the web is told, and the check at delivery ---------------------------------------------------------

    [Fact]
    public async Task The_web_view_says_a_code_is_needed_for_a_strict_shell_and_interpreter_run_and_not_for_a_plain_argv_run()
    {
        var team = await StrictTeamAsync(api);
        var shell = await CreateAsync(team, RemoteKit.Shell(team.TargetId, RemoteKit.NewKey()));
        var interpreter = await CreateAsync(team, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), Interpreter));
        var plain = await CreateAsync(team, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()));

        foreach (var (id, expected) in new[] { (shell, true), (interpreter, true), (plain, false) })
        {
            var view = await team.Owner.GetJsonAsync($"/api/runs/{id}");
            Assert.Equal(expected, view.GetProperty("needsCode").GetBoolean());
        }
    }

    [Fact]
    public async Task A_shell_command_approved_with_a_click_is_cancelled_when_its_target_becomes_strict_before_delivery_but_a_plain_argv_run_is_not()
    {
        var team = await ScriptKit.TeamAsync(api, "test", "application", ExecLevels.Shell);
        var shell = RemoteKit.Shell(team.TargetId, RemoteKit.NewKey());
        var plain = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey());
        var shellId = await CreateAsync(team, shell);
        var plainId = await CreateAsync(team, plain);
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(team.Owner, shellId, shell)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(team.Owner, plainId, plain)).StatusCode);
        await ScriptKit.SetRoleAsync(team, "production", "application");

        await using var scope = api.Services.CreateAsyncScope();
        var waiting = await RunNotices.WaitingForAsync(scope.ServiceProvider.GetRequiredService<Api.Data.MonitorDb>(), team.TargetId, api.Clock.GetUtcNow(), CancellationToken.None,
            api.Services.GetRequiredService<Api.Streaming.Broker>());

        Assert.Equal(plainId, Assert.Single(waiting).Id);
        var row = await RemoteKit.RunRowAsync(api, shellId);
        Assert.Equal((RunStatuses.Cancelled, RemoteErrors.RoleChanged), (row.Status, row.Error));
    }

    // ---- grants and jobs ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_grant_on_a_strict_target_needs_a_code_even_after_a_fresh_sign_in_and_without_two_step_sign_in_it_cannot_be_approved()
    {
        var team = await StrictTeamAsync(api);
        var grant = await RemoteKit.RequestGrantAsync(team.Requester, RemoteKit.GrantAsk(team.TargetId));

        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync($"/api/grants/{grant.Id}/approve", new { }), HttpStatusCode.Forbidden, RemoteErrors.TotpRequired);
    }

    [Fact]
    public async Task A_grant_on_a_strict_target_is_approved_only_with_a_valid_code_and_a_recent_sign_in_alone_is_not_enough()
    {
        var team = await StrictTeamAsync(api);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        var grant = await RemoteKit.RequestGrantAsync(team.Requester, RemoteKit.GrantAsk(team.TargetId));

        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync($"/api/grants/{grant.Id}/approve", new { }), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync($"/api/grants/{grant.Id}/approve", new { code = "000000" }), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/grants/{grant.Id}/approve", new { code = RemoteKit.TotpNow(api, secret) })).StatusCode);
    }

    [Fact]
    public async Task A_grant_on_a_target_that_is_not_strict_is_still_approved_by_a_recent_sign_in()
    {
        var team = await ScriptKit.TeamAsync(api, "test", "application", ExecLevels.Argv);
        var grant = await RemoteKit.RequestGrantAsync(team.Requester, RemoteKit.GrantAsk(team.TargetId));
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/grants/{grant.Id}/approve", new { })).StatusCode);
    }

    [Fact]
    public async Task A_job_on_a_strict_target_needs_a_code_and_without_two_step_sign_in_cannot_be_approved()
    {
        var team = await StrictTeamAsync(api);
        var job = await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs", new JobProposal(team.TargetId, "Run tests", ["/opt/ci/run-tests", "--suite", "unit"], "/opt/ci", 300, "needs a test run"), TestUser.Json);
        Assert.Equal(HttpStatusCode.OK, job.StatusCode);
        var id = (await job.Content.ReadFromJsonAsync<JobView>(TestUser.Json))!.Id;

        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync($"/api/jobs/{id}/approve", new { }), HttpStatusCode.Forbidden, RemoteErrors.TotpRequired);

        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync($"/api/jobs/{id}/approve", new { }), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/jobs/{id}/approve", new { code = RemoteKit.TotpNow(api, secret) })).StatusCode);
    }
}
