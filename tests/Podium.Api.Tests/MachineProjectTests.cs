using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Remote;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>Projects on a machine, runs that name a project and a kind, and the detail of a machine (ADR-0008 §3a′).</summary>
[Collection(ApiGroup.Name)]
public sealed class MachineProjectTests(ApiFactory api)
{
    private static ProjectFact Fact(string key, string? sha = "0123abcd", string? hmac = "ab12", string source = ProjectSources.Listed) =>
        new(key, key.ToUpperInvariant(), hmac, "main", sha, "v1.2.0", source);

    private static async Task SendProfileAsync(RemoteTeam team, params ProjectFact[] facts) =>
        (await team.Target.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(ExecLevels.Argv, false, null, null, 2, Projects: facts), TestUser.Json))
            .EnsureSuccessStatusCode();

    private static async Task<JsonElement> DetailAsync(TestUser reader, RemoteTeam team) => await reader.GetJsonAsync($"/api/agents/{team.TargetId}/detail");

    private static async Task<Guid> FinishedRunAsync(RemoteTeam team, string project, string? kind, string output, string status = RunStatuses.Succeeded)
    {
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()) with { ProjectKey = project, Kind = kind };
        var created = await RemoteKit.CreateAsync(team.Requester, req);
        (await RemoteKit.ApproveAsync(team.Owner, created.Id, req)).EnsureSuccessStatusCode();
        (await RemoteKit.StatusAsync(team.Target, created.Id, new RunStatusUpdate(RunStatuses.Running))).EnsureSuccessStatusCode();
        (await team.Target.Http.PostAsJsonAsync($"/api/agent/runs/{created.Id}/output", new[] { new RunOutputChunk(0, RunStreams.Stdout, output) }, TestUser.Json)).EnsureSuccessStatusCode();
        (await RemoteKit.StatusAsync(team.Target, created.Id, new RunStatusUpdate(status, status == RunStatuses.Succeeded ? 0 : 1))).EnsureSuccessStatusCode();
        return created.Id;
    }

    [Fact]
    public async Task Facts_the_agent_read_from_project_folders_appear_in_the_detail_and_a_listed_row_keeps_its_source()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendProfileAsync(team, Fact("shop"), Fact("blog", sha: null, hmac: null));
        var projects = (await DetailAsync(team.Admin, team)).GetProperty("projects");
        Assert.Equal(2, projects.GetArrayLength());
        var shop = projects.EnumerateArray().Single(p => p.GetProperty("key").GetString() == "shop");
        Assert.Equal(("main", "0123abcd", "v1.2.0", "listed"), (shop.GetProperty("branch").GetString(), shop.GetProperty("headSha").GetString(),
            shop.GetProperty("tag").GetString(), shop.GetProperty("source").GetString()));
    }

    [Fact]
    public async Task A_commit_that_is_not_hex_and_a_key_with_control_characters_never_reach_the_database()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendProfileAsync(team, Fact("ok", sha: "not-a-sha; drop"), Fact("bad\u0007key"), Fact(new string('k', 201)));
        await using var db = api.Db();
        var rows = await db.MachineProjects.AsNoTracking().Where(p => p.AgentId == team.TargetId).ToListAsync();
        var only = Assert.Single(rows);
        Assert.Equal("ok", only.ProjectKey);
        Assert.Null(only.HeadSha);
    }

    [Fact]
    public async Task A_project_a_session_reported_is_listed_on_the_machine_with_the_branch_it_was_on()
    {
        var team = await RemoteKit.TeamAsync(api);
        (await team.Target.SendAsync(TestAgent.Hook("s-" + Guid.NewGuid().ToString("N"), "Stop", new { }, DateTimeOffset.UtcNow, "shop"))).EnsureSuccessStatusCode();
        var shop = (await DetailAsync(team.Admin, team)).GetProperty("projects").EnumerateArray().Single();
        Assert.Equal(("shop", "session", "main"), (shop.GetProperty("key").GetString(), shop.GetProperty("source").GetString(), shop.GetProperty("branch").GetString()));
    }

    [Fact]
    public async Task A_run_that_names_a_project_and_a_kind_shows_under_that_project_with_its_result()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendProfileAsync(team, Fact("shop"));
        var id = await FinishedRunAsync(team, "shop", RunKinds.Build, "built");
        var runs = (await DetailAsync(team.Owner, team)).GetProperty("projects").EnumerateArray().Single().GetProperty("lastRuns");
        var run = Assert.Single(runs.EnumerateArray());
        Assert.Equal((RunKinds.Build, RunStatuses.Succeeded, 0), (run.GetProperty("kind").GetString(), run.GetProperty("status").GetString(), run.GetProperty("exitCode").GetInt32()));
        var row = await RemoteKit.RunRowAsync(api, id);
        Assert.Equal(("shop", RunKinds.Build), (row.ProjectKey, row.Kind));
    }

    [Fact]
    public async Task A_finished_test_run_carries_the_counts_parsed_from_its_output_and_another_kind_does_not()
    {
        var team = await RemoteKit.TeamAsync(api);
        var test = await FinishedRunAsync(team, "shop", RunKinds.Test, "Passed!  - Failed:     1, Passed:    41, Skipped:     2, Total:    44, Duration: 3 s - A.dll");
        var build = await FinishedRunAsync(team, "shop", RunKinds.Build, "Passed!  - Failed:     1, Passed:    41, Skipped:     2, Total:    44, Duration: 3 s - A.dll");
        var t = await RemoteKit.RunRowAsync(api, test);
        Assert.Equal((41, 1, 2), (t.TestsPassed, t.TestsFailed, t.TestsSkipped));
        var b = await RemoteKit.RunRowAsync(api, build);
        Assert.Null(b.TestsPassed);
    }

    [Fact]
    public async Task An_unknown_kind_or_an_unsafe_project_key_is_refused_with_its_own_code()
    {
        var team = await RemoteKit.TeamAsync(api);
        var kind = await RemoteKit.PostRunAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()) with { Kind = "benchmark" });
        await RemoteKit.AssertProblemAsync(kind, HttpStatusCode.BadRequest, RunRules.BadKind);
        var key = await RemoteKit.PostRunAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()) with { ProjectKey = "a\nb" });
        await RemoteKit.AssertProblemAsync(key, HttpStatusCode.BadRequest, RunRules.BadProject);
    }

    [Fact]
    public async Task The_detail_shows_the_note_to_every_member_and_the_services_only_to_the_owner_and_admins_after_the_opt_in()
    {
        var team = await RemoteKit.TeamAsync(api);
        var viewer = await RemoteKit.MemberAsync(api, team.Admin, "mp-viewer", "viewer");
        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/note", new { note = "restart with care" })).EnsureSuccessStatusCode();
        (await team.Owner.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/services", new { share = true })).EnsureSuccessStatusCode();
        await team.Target.Http.PutAsJsonAsync("/api/agent/profile",
            new AgentProfile(ExecLevels.Argv, false, null, null, 2, Services: [new ServiceInfo(5432, "tcp", "postgres")]), TestUser.Json);

        foreach (var reader in new[] { team.Admin, team.Owner, team.Other, viewer })
        {
            Assert.Equal("restart with care", (await DetailAsync(reader, team)).GetProperty("note").GetString());
        }

        foreach (var allowed in new[] { team.Admin, team.Owner })
        {
            Assert.Equal(5432, (await DetailAsync(allowed, team)).GetProperty("services")[0].GetProperty("port").GetInt32());
        }

        foreach (var denied in new[] { team.Other, viewer })
        {
            var services = (await DetailAsync(denied, team)).GetProperty("services");
            Assert.Equal(JsonValueKind.Null, services.ValueKind);
        }
    }

    [Fact]
    public async Task Someone_outside_the_workspace_gets_not_found_for_the_detail()
    {
        var team = await RemoteKit.TeamAsync(api);
        var outsider = await api.NewClient().SignedUpAsync("mp-outsider");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Http.GetAsync($"/api/agents/{team.TargetId}/detail")).StatusCode);
    }

    [Fact]
    public async Task The_claude_path_stays_out_of_the_detail_tools()
    {
        var team = await RemoteKit.TeamAsync(api);
        await team.Target.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(ExecLevels.Argv, false, null, null, 2,
            Tools: [new ToolInfo(ToolNames.Claude, "~/.local/bin/claude", "2.1.0", true)]), TestUser.Json);
        var text = (await DetailAsync(team.Other, team)).GetRawText();
        Assert.DoesNotContain(".local/bin/claude", text);
    }
}
