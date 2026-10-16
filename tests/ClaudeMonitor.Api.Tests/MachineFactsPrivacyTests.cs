using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Retention, export and account deletion of the machine facts (ADR-0008 §3a′): project rows, listening services and the project key of a run.</summary>
[Collection(ApiGroup.Name)]
public sealed class MachineFactsPrivacyTests(ApiFactory api)
{
    private static async Task SendProfileAsync(RemoteTeam team, params ProjectFact[] facts) =>
        (await team.Target.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(ExecLevels.Argv, false, null, null, 2, Projects: facts), TestUser.Json))
            .EnsureSuccessStatusCode();

    private static ProjectFact Fact(string key) => new(key, key, "ab12", "main", "0123abcd", null, ProjectSources.Listed);

    [Fact]
    public async Task A_project_not_seen_for_the_retention_period_is_forgotten_and_a_recent_one_stays()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendProfileAsync(team, Fact("old-project"), Fact("recent-project"));
        // Seen 100 and 40 days ago; retention is 90 (set in the database: advancing the clock would also expire the agent's token).
        await using (var set = api.Db())
        {
            var now = api.Clock.GetUtcNow();
            await set.MachineProjects.Where(p => p.ProjectKey == "old-project").ExecuteUpdateAsync(s => s.SetProperty(p => p.LastSeenAt, now.AddDays(-100)));
            await set.MachineProjects.Where(p => p.ProjectKey == "recent-project").ExecuteUpdateAsync(s => s.SetProperty(p => p.LastSeenAt, now.AddDays(-40)));
        }

        await RemoteKit.HousekeepAsync(api);

        await using var db = api.Db();
        var keys = await db.MachineProjects.AsNoTracking().Where(p => p.AgentId == team.TargetId).Select(p => p.ProjectKey).ToListAsync();
        Assert.Equal(["recent-project"], keys);
    }

    [Fact]
    public async Task The_export_holds_the_project_rows_of_the_users_machines_and_the_project_key_of_runs()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendProfileAsync(team, Fact("shop"));
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()) with { ProjectKey = "shop", Kind = RunKinds.Build };
        var created = await RemoteKit.CreateAsync(team.Requester, req);

        var export = JsonDocument.Parse(await (await team.Owner.Http.GetAsync("/api/me/export")).Content.ReadAsStringAsync()).RootElement;

        var project = Assert.Single(export.GetProperty("machineProjects").EnumerateArray());
        Assert.Equal(("shop", "main", "0123abcd"), (project.GetProperty("projectKey").GetString(), project.GetProperty("branch").GetString(), project.GetProperty("headSha").GetString()));
        Assert.DoesNotContain("pathHmac", project.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var asker = JsonDocument.Parse(await (await team.Admin.Http.GetAsync("/api/me/export")).Content.ReadAsStringAsync()).RootElement;
        var run = asker.GetProperty("remoteRuns").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == created.Id);
        Assert.Equal(("shop", "build"), (run.GetProperty("projectKey").GetString(), run.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task Deleting_the_owners_account_removes_the_projects_and_the_services_of_their_machine_and_empties_the_project_key_of_their_runs()
    {
        var team = await RemoteKit.TeamAsync(api);
        (await team.Owner.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/services", new { share = true })).EnsureSuccessStatusCode();
        (await team.Target.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(ExecLevels.Argv, false, null, null, 2,
            Services: [new ServiceInfo(5432, "tcp", "postgres")], Projects: [Fact("shop")]), TestUser.Json)).EnsureSuccessStatusCode();
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()) with { ProjectKey = "shop", Kind = RunKinds.Test };
        var created = await RemoteKit.CreateAsync(team.Requester, req);

        var deleted = await team.Owner.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" });
        Assert.Equal(System.Net.HttpStatusCode.NoContent, deleted.StatusCode);

        await using var db = api.Db();
        Assert.False(await db.MachineProjects.AnyAsync(p => p.AgentId == team.TargetId));
        var agent = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId);
        Assert.Equal((false, null), (agent.ShareServices, agent.Services));
        Assert.Null((await RemoteKit.RunRowAsync(api, created.Id)).ProjectKey);
    }
}
