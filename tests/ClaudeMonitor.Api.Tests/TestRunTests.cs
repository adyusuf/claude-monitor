using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>"Start the test" for a project (ADR-0011 §7a): the command comes from the machine, the web names the project, the run is an ordinary one that waits for its owner.</summary>
[Collection(ApiGroup.Name)]
public sealed class TestRunTests(ApiFactory api)
{
    private static readonly string[] Command = ["/usr/bin/dotnet", "test", "--no-build"];
    private const string Folder = "/work/shop";

    private static ProjectFact Fact(string key = "shop", IReadOnlyList<string>? argv = null, string? cwd = Folder) =>
        new(key, key, "ab12", "main", "0123abcd", null, ProjectSources.Listed, argv, cwd);

    private static async Task SendProfileAsync(RemoteTeam team, params ProjectFact[] facts) => await SendProfileAsync(team, ExecLevels.Argv, facts);

    private static async Task SendProfileAsync(RemoteTeam team, string execLevel, params ProjectFact[] facts) =>
        (await team.Target.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(execLevel, false, null, null, 2, Projects: facts), TestUser.Json)).EnsureSuccessStatusCode();

    private static Task<HttpResponseMessage> Start(TestUser user, RemoteTeam team, string project = "shop") =>
        user.PostAsync($"/api/agents/{team.TargetId}/test-runs", new TestRunRequest(project));

    private static async Task<JsonElement> ProjectAsync(TestUser reader, RemoteTeam team) =>
        (await reader.GetJsonAsync($"/api/agents/{team.TargetId}/detail")).GetProperty("projects").EnumerateArray().Single();

    [Fact]
    public async Task The_command_is_shown_to_the_machines_owner_only_and_everyone_else_sees_that_there_is_one()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendProfileAsync(team, Fact(argv: Command));

        var owner = await ProjectAsync(team.Owner, team);
        Assert.True(owner.GetProperty("hasTest").GetBoolean());
        Assert.Equal(Command, owner.GetProperty("testArgv").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(Folder, owner.GetProperty("testCwd").GetString());
        var admin = await ProjectAsync(team.Admin, team);
        Assert.True(admin.GetProperty("hasTest").GetBoolean());
        Assert.Equal(JsonValueKind.Null, admin.GetProperty("testArgv").ValueKind);
        Assert.Equal(JsonValueKind.Null, admin.GetProperty("testCwd").ValueKind);
    }

    [Fact]
    public async Task The_owner_starts_an_ordinary_argv_run_of_kind_test_that_waits_for_approval_and_can_then_be_approved()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendProfileAsync(team, Fact(argv: Command));

        var response = await Start(team.Owner, team);

        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<RunCreated>(TestUser.Json))!;
        Assert.Equal(RunStatuses.PendingApproval, created.Status);
        await using (var db = api.Db())
        {
            var run = await db.RemoteRuns.AsNoTracking().SingleAsync(r => r.Id == created.Id);
            Assert.Equal((RunModes.Argv, RunKinds.Test, "shop", Folder, team.TargetId, TestRuns.TimeoutSeconds), (run.Mode, run.Kind, run.ProjectKey, run.Cwd, run.TargetAgentId, run.TimeoutSeconds));
            Assert.Equal(Command, run.Argv);
        }

        var asked = new RunCreate("x", team.TargetId, RunModes.Argv, Command, null, Folder, TestRuns.TimeoutSeconds, TestRuns.Reason, ProjectKey: "shop", Kind: RunKinds.Test);
        (await RemoteKit.ApproveAsync(team.Owner, created.Id, asked)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Only_the_machines_owner_may_start_it()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendProfileAsync(team, Fact(argv: Command));

        await RemoteKit.AssertProblemAsync(await Start(team.Admin, team), HttpStatusCode.Forbidden, AgentActionErrors.OwnerOnly);
        await RemoteKit.AssertProblemAsync(await Start(team.Other, team), HttpStatusCode.Forbidden, AgentActionErrors.OwnerOnly);
        await using var db = api.Db();
        Assert.Empty(await db.RemoteRuns.AsNoTracking().Where(r => r.TargetAgentId == team.TargetId).ToListAsync());
    }

    [Fact]
    public async Task A_project_without_a_reported_command_or_with_a_stale_one_starts_nothing()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendProfileAsync(team, Fact(argv: Command), Fact("blog"));

        await RemoteKit.AssertProblemAsync(await Start(team.Owner, team, "blog"), HttpStatusCode.Conflict, AgentActionErrors.NoTestCommand);
        await RemoteKit.AssertProblemAsync(await Start(team.Owner, team, "unknown"), HttpStatusCode.Conflict, AgentActionErrors.NoTestCommand);
        api.Clock.Advance(TestRuns.FactsMaxAge + TimeSpan.FromMinutes(1));
        await RemoteKit.AssertProblemAsync(await Start(team.Owner, team), HttpStatusCode.Conflict, AgentActionErrors.NoTestCommand);
        await using var db = api.Db();
        Assert.Empty(await db.RemoteRuns.AsNoTracking().Where(r => r.TargetAgentId == team.TargetId).ToListAsync());
    }

    [Fact]
    public async Task A_later_report_without_the_command_removes_it()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendProfileAsync(team, Fact(argv: Command));
        await SendProfileAsync(team, Fact());

        Assert.False((await ProjectAsync(team.Owner, team)).GetProperty("hasTest").GetBoolean());
        await RemoteKit.AssertProblemAsync(await Start(team.Owner, team), HttpStatusCode.Conflict, AgentActionErrors.NoTestCommand);
    }

    [Fact]
    public async Task A_command_that_breaks_the_rules_is_never_stored()
    {
        var team = await RemoteKit.TeamAsync(api);
        foreach (var argv in new[] { Array.Empty<string>(), [""], ["/usr/bin/dotnet", "te\u0007st"] })
        {
            await SendProfileAsync(team, Fact(argv: argv));
            await using var db = api.Db();
            Assert.Null((await db.MachineProjects.AsNoTracking().SingleAsync(p => p.AgentId == team.TargetId)).TestArgv);
        }
    }

    [Fact]
    public async Task A_command_with_too_many_or_too_long_elements_is_never_stored_and_a_folder_with_control_characters_drops_it()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendProfileAsync(team, Fact(argv: [.. Enumerable.Repeat("/usr/bin/x", ProjectTestRules.MaxElements + 1)]));
        await using var db = api.Db();
        Assert.Null((await db.MachineProjects.AsNoTracking().SingleAsync(p => p.AgentId == team.TargetId)).TestArgv);
        await SendProfileAsync(team, Fact(argv: [new string('a', ProjectTestRules.MaxElementLength + 1)]));
        await SendProfileAsync(team, Fact(argv: Command, cwd: "/work/\nshop"));
        Assert.Null((await api.Db().MachineProjects.AsNoTracking().SingleAsync(p => p.AgentId == team.TargetId)).TestCwd);
    }

    [Fact]
    public async Task A_machine_that_takes_no_runs_gives_the_usual_refusal()
    {
        var team = await RemoteKit.TeamAsync(api, execLevel: ExecLevels.Off);
        await SendProfileAsync(team, ExecLevels.Off, Fact(argv: Command));

        var response = await Start(team.Owner, team);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
