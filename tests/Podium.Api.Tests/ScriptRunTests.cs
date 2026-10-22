using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Remote;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>Script runs (ADR-0008 §3c): who may create one, what it is bound to, and which approvals it needs.</summary>
[Collection(ApiGroup.Name)]
public sealed class ScriptRunTests(ApiFactory api)
{
    private static async Task<RunCreated> CreateAsync(RemoteTeam team, RunCreate req)
    {
        var response = await RemoteKit.PostRunAsync(team.Requester, req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RunCreated>(TestUser.Json))!;
    }

    [Fact]
    public async Task A_script_on_a_shell_level_target_with_a_role_and_a_trusted_interpreter_waits_for_the_owner_and_freezes_the_role_the_script_and_the_cap()
    {
        var team = await ScriptKit.TeamAsync(api, "test", "application");
        var req = ScriptKit.Script(team.TargetId, args: ["--flag", "a b"]);

        var created = await CreateAsync(team, req);

        Assert.Equal(RunStatuses.PendingApproval, created.Status);
        var row = await RemoteKit.RunRowAsync(api, created.Id);
        Assert.Equal((RunModes.Script, "sh", ScriptKit.Body, "test", "application"), (row.Mode, row.ScriptInterpreter, row.ScriptBody, row.TargetEnvironment, row.TargetFunction));
        Assert.Equal(["--flag", "a b"], row.ScriptArgs);
        Assert.Equal(8L * 1024 * 1024, row.OutputCap);
        Assert.Null(row.Argv);
        Assert.Null(row.ShellCommand);
        Assert.Null(row.Cwd);
    }

    [Fact]
    public async Task A_target_nobody_has_classified_refuses_a_script_even_when_the_machine_claims_a_role_and_an_argv_run_is_unchanged()
    {
        var team = await ScriptKit.TeamAsync(api, environment: null, function: null);
        await ScriptKit.ProfileAsync(team, ExecLevels.Shell, [ScriptKit.Sh], claimedEnvironment: "test");

        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, ScriptKit.Script(team.TargetId)), HttpStatusCode.Conflict, RemoteErrors.RoleNotSet);
        Assert.Equal(HttpStatusCode.OK, (await RemoteKit.PostRunAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()))).StatusCode);
    }

    [Fact]
    public async Task A_script_needs_a_trusted_interpreter_the_target_reported()
    {
        var untrusted = await ScriptKit.TeamAsync(api, interpreters: [ScriptKit.Sh with { Trusted = false }]);
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(untrusted.Requester, ScriptKit.Script(untrusted.TargetId)), HttpStatusCode.Conflict, RemoteErrors.InterpreterMissing);

        var none = await ScriptKit.TeamAsync(api, interpreters: []);
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(none.Requester, ScriptKit.Script(none.TargetId)), HttpStatusCode.Conflict, RemoteErrors.InterpreterMissing);

        var other = await ScriptKit.TeamAsync(api); // reports sh only
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(other.Requester, ScriptKit.Script(other.TargetId, interpreter: ScriptInterpreters.Python)),
            HttpStatusCode.Conflict, RemoteErrors.InterpreterMissing);
    }

    [Fact]
    public async Task An_argv_level_target_never_takes_a_script()
    {
        var team = await ScriptKit.TeamAsync(api, execLevel: ExecLevels.Argv);
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, ScriptKit.Script(team.TargetId)), HttpStatusCode.Conflict, RemoteErrors.TargetCannotRun);
    }

    [Theory]
    [InlineData("", RemoteErrors.InvalidScript)]
    [InlineData("   \n", RemoteErrors.InvalidScript)]
    [InlineData("echo \0 nul", RemoteErrors.InvalidScript)]
    public async Task An_empty_blank_or_NUL_script_is_refused(string body, string code)
    {
        var team = await ScriptKit.TeamAsync(api);
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, ScriptKit.Script(team.TargetId, body: body)), HttpStatusCode.BadRequest, code);
    }

    [Fact]
    public async Task A_script_over_the_limit_an_unknown_interpreter_too_many_arguments_or_a_working_directory_are_refused()
    {
        var team = await ScriptKit.TeamAsync(api);
        var max = ScriptInterpreters.BodyMax;
        Assert.Equal(HttpStatusCode.OK, (await RemoteKit.PostRunAsync(team.Requester, ScriptKit.Script(team.TargetId, body: new string('a', max)))).StatusCode);
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, ScriptKit.Script(team.TargetId, body: new string('a', max + 1))), HttpStatusCode.BadRequest, RemoteErrors.InvalidScript);
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, ScriptKit.Script(team.TargetId, interpreter: "perl")), HttpStatusCode.BadRequest, RemoteErrors.InvalidInterpreter);
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, ScriptKit.Script(team.TargetId, args: [.. Enumerable.Repeat("a", ScriptInterpreters.ArgsMax + 1)])), HttpStatusCode.BadRequest, RemoteErrors.InvalidScript);
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, ScriptKit.Script(team.TargetId) with { Cwd = "/tmp" }), HttpStatusCode.BadRequest, RunRules.BadCwd);
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, ScriptKit.Script(team.TargetId) with { Argv = ["/bin/ls"] }), HttpStatusCode.BadRequest, RunRules.BadCommand);
    }

    [Fact]
    public async Task Script_fields_on_an_argv_or_a_shell_run_are_refused()
    {
        var team = await ScriptKit.TeamAsync(api);
        var argv = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()) with { ScriptBody = "echo x" };
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, argv), HttpStatusCode.BadRequest, RunRules.BadCommand);
    }

    [Fact]
    public async Task A_script_never_matches_a_grant_or_a_job_it_always_waits_for_the_owner()
    {
        var team = await ScriptKit.TeamAsync(api);
        await RemoteKit.ActiveGrantAsync(team);

        var created = await CreateAsync(team, ScriptKit.Script(team.TargetId));

        Assert.Equal(RunStatuses.PendingApproval, created.Status);
        Assert.Null(created.GrantId);
    }

    [Fact]
    public async Task A_retried_create_returns_the_same_run_and_a_different_script_under_the_same_key_is_a_conflict()
    {
        var team = await ScriptKit.TeamAsync(api);
        var req = ScriptKit.Script(team.TargetId, "same-key");
        var first = await CreateAsync(team, req);
        Assert.Equal(first.Id, (await CreateAsync(team, req)).Id);
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, req with { ScriptBody = "echo other\n" }), HttpStatusCode.Conflict, RemoteErrors.Mismatch);
    }

    [Fact]
    public async Task An_approval_binds_to_the_exact_script_and_role_and_a_wrong_hash_is_a_mismatch()
    {
        var team = await ScriptKit.TeamAsync(api, "test", "application");
        var req = ScriptKit.Script(team.TargetId);
        var created = await CreateAsync(team, req);

        await RemoteKit.AssertProblemAsync(await ScriptKit.ApproveAsync(team.Owner, created.Id, req with { ScriptBody = "echo changed\n" }, "test", "application"), HttpStatusCode.Conflict, RemoteErrors.Mismatch);
        await RemoteKit.AssertProblemAsync(await ScriptKit.ApproveAsync(team.Owner, created.Id, req, "production", "application"), HttpStatusCode.Conflict, RemoteErrors.Mismatch);
        Assert.Equal(HttpStatusCode.NoContent, (await ScriptKit.ApproveAsync(team.Owner, created.Id, req, "test", "application")).StatusCode);
    }

    [Fact]
    public async Task On_a_target_that_is_not_strict_an_owner_without_two_step_sign_in_approves_with_a_click_and_the_run_is_not_marked()
    {
        var team = await ScriptKit.TeamAsync(api, "test", "application");
        var req = ScriptKit.Script(team.TargetId);
        var created = await CreateAsync(team, req);

        Assert.Equal(HttpStatusCode.NoContent, (await ScriptKit.ApproveAsync(team.Owner, created.Id, req, "test", "application")).StatusCode);

        var row = await RemoteKit.RunRowAsync(api, created.Id);
        Assert.Equal((RunStatuses.Approved, false), (row.Status, row.ApprovedWithTotp));
    }

    [Theory]
    [InlineData("production", "application")]
    [InlineData("test", "database")]
    public async Task On_a_strict_target_an_owner_without_two_step_sign_in_cannot_approve_and_the_run_stays_pending(string environment, string function)
    {
        var team = await ScriptKit.TeamAsync(api, environment, function);
        var req = ScriptKit.Script(team.TargetId);
        var created = await CreateAsync(team, req);

        await RemoteKit.AssertProblemAsync(await ScriptKit.ApproveAsync(team.Owner, created.Id, req, environment, function), HttpStatusCode.Forbidden, RemoteErrors.TotpRequired);

        Assert.Equal(RunStatuses.PendingApproval, (await RemoteKit.RunRowAsync(api, created.Id)).Status);
    }

    [Fact]
    public async Task On_a_strict_target_an_owner_with_two_step_sign_in_needs_a_valid_code_and_the_run_is_then_marked_as_approved_with_one()
    {
        var team = await ScriptKit.TeamAsync(api, "production", "application");
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        var req = ScriptKit.Script(team.TargetId);
        var created = await CreateAsync(team, req);

        await RemoteKit.AssertProblemAsync(await ScriptKit.ApproveAsync(team.Owner, created.Id, req, "production", "application"), HttpStatusCode.Forbidden, RemoteErrors.MfaRequired);
        await RemoteKit.AssertProblemAsync(await ScriptKit.ApproveAsync(team.Owner, created.Id, req, "production", "application", "000000"), HttpStatusCode.Forbidden, RemoteErrors.MfaRequired);
        Assert.Equal(HttpStatusCode.NoContent, (await ScriptKit.ApproveAsync(team.Owner, created.Id, req, "production", "application", RemoteKit.TotpNow(api, secret))).StatusCode);

        Assert.True((await RemoteKit.RunRowAsync(api, created.Id)).ApprovedWithTotp);
    }

    [Fact]
    public async Task A_role_that_changed_after_the_card_was_made_is_a_conflict_and_the_owner_must_look_again()
    {
        var team = await ScriptKit.TeamAsync(api, "test", "application");
        var req = ScriptKit.Script(team.TargetId);
        var created = await CreateAsync(team, req);
        await ScriptKit.SetRoleAsync(team, "production", "application");

        await RemoteKit.AssertProblemAsync(await ScriptKit.ApproveAsync(team.Owner, created.Id, req, "test", "application"), HttpStatusCode.Conflict, RemoteErrors.RoleChanged);
        Assert.Equal(RunStatuses.PendingApproval, (await RemoteKit.RunRowAsync(api, created.Id)).Status);
    }

    [Fact]
    public async Task A_script_approved_without_a_code_is_cancelled_with_role_changed_when_its_target_has_become_strict_before_delivery()
    {
        var team = await ScriptKit.TeamAsync(api, "test", "application");
        var req = ScriptKit.Script(team.TargetId);
        var created = await CreateAsync(team, req);
        Assert.Equal(HttpStatusCode.NoContent, (await ScriptKit.ApproveAsync(team.Owner, created.Id, req, "test", "application")).StatusCode);
        await ScriptKit.SetRoleAsync(team, "production", "application");

        await using var scope = api.Services.CreateAsyncScope();
        var waiting = await RunNotices.WaitingForAsync(scope.ServiceProvider.GetRequiredService<Api.Data.PodiumDb>(), team.TargetId, api.Clock.GetUtcNow(), CancellationToken.None,
            api.Services.GetRequiredService<Api.Streaming.Broker>());

        Assert.Empty(waiting);
        var row = await RemoteKit.RunRowAsync(api, created.Id);
        Assert.Equal((RunStatuses.Cancelled, RemoteErrors.RoleChanged), (row.Status, row.Error));
    }

    [Fact]
    public async Task A_script_approved_with_a_code_is_still_delivered_after_its_target_became_strict()
    {
        var team = await ScriptKit.TeamAsync(api, "test", "application");
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        var req = ScriptKit.Script(team.TargetId);
        var created = await CreateAsync(team, req);
        Assert.Equal(HttpStatusCode.NoContent, (await ScriptKit.ApproveAsync(team.Owner, created.Id, req, "test", "application", RemoteKit.TotpNow(api, secret))).StatusCode);
        await ScriptKit.SetRoleAsync(team, "production", "application");

        await using var scope = api.Services.CreateAsyncScope();
        var waiting = await RunNotices.WaitingForAsync(scope.ServiceProvider.GetRequiredService<Api.Data.PodiumDb>(), team.TargetId, api.Clock.GetUtcNow(), CancellationToken.None,
            api.Services.GetRequiredService<Api.Streaming.Broker>());

        var message = Assert.Single(waiting);
        Assert.Equal((RunModes.Script, "sh", ScriptKit.Body, true), (message.Mode, message.ScriptInterpreter, message.ScriptBody, message.ApprovedWithTotp));
        Assert.Equal(8L * 1024 * 1024, message.OutputCap);
    }

    [Fact]
    public async Task The_web_view_gives_the_owner_the_script_the_interpreter_path_and_whether_a_code_is_needed()
    {
        var team = await ScriptKit.TeamAsync(api, "production", "application");
        var created = await CreateAsync(team, ScriptKit.Script(team.TargetId, args: ["one"]));

        var run = await team.Owner.GetJsonAsync($"/api/runs/{created.Id}");

        Assert.Equal(("script", "sh", ScriptKit.Body, "/bin/sh"), (run.GetProperty("mode").GetString(), run.GetProperty("scriptInterpreter").GetString(), run.GetProperty("scriptBody").GetString(), run.GetProperty("interpreterPath").GetString()));
        Assert.True(run.GetProperty("strict").GetBoolean());
        Assert.True(run.GetProperty("needsCode").GetBoolean());
        Assert.True(run.GetProperty("canDecide").GetBoolean());

        var other = await team.Other.GetJsonAsync($"/api/runs/{created.Id}");
        Assert.Equal(JsonValueKind.Null, other.GetProperty("scriptBody").ValueKind);
    }
}
