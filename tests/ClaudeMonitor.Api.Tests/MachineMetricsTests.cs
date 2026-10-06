using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>What an agent reports about its machine (ADR-0004): profile, metric samples and alerts, and how they are read back.</summary>
[Collection(ApiGroup.Name)]
public sealed class MachineMetricsTests(ApiFactory api)
{
    private DateTimeOffset Now => api.Clock.GetUtcNow();

    private static MetricSample Sample(DateTimeOffset at, double cpu = 12.5, long used = 400, long total = 1000, params DiskSample[] disks) =>
        new(at, cpu, used, total, disks);

    private static async Task<int> StoredAsync(TestAgent agent, params MetricSample[] samples)
    {
        var response = await agent.Http.PostAsJsonAsync("/api/agent/metrics", new MetricsReport(samples), TestUser.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stored").GetInt32();
    }

    private static Task<HttpResponseMessage> Alert(TestAgent agent, params AlertReport[] reports) =>
        agent.Http.PostAsJsonAsync("/api/agent/alerts", reports, TestUser.Json);

    private AlertReport Report(string kind, string state, string subject = "", double value = 95, double threshold = 90) =>
        new(kind, subject, state, value, threshold, Now);

    private async Task<(TestUser User, TestAgent Agent)> LoneAgentAsync(string tag)
    {
        var user = await api.NewClient().SignedUpAsync(tag);
        return (user, await user.ConnectAgentAsync());
    }

    [Fact]
    public async Task The_profile_sets_the_exec_level_and_service_mode_and_an_unknown_level_means_off()
    {
        var (_, agent) = await LoneAgentAsync("mm-profile");
        var id = agent.Tokens.AgentId;
        await using (var db = api.Db())
        {
            var fresh = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == id);
            Assert.Equal((ExecLevels.Off, false), (fresh.ExecLevel, fresh.ServiceMode));
        }

        await RemoteKit.ProfileAsync(agent, ExecLevels.Shell, service: true);
        await using (var db = api.Db())
        {
            var row = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == id);
            Assert.Equal((ExecLevels.Shell, true), (row.ExecLevel, row.ServiceMode));
            Assert.Equal(Now, row.ProfileAt!.Value, TimeSpan.FromMilliseconds(5));
        }

        foreach (var unknown in new[] { "root", "SHELL", "", "argv ", "admin" })
        {
            await RemoteKit.ProfileAsync(agent, unknown);
            await using var db = api.Db();
            Assert.Equal(ExecLevels.Off, (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == id)).ExecLevel);
        }

        var machines = await agent.Http.GetFromJsonAsync<List<MachineView>>("/api/agent/machines", TestUser.Json);
        Assert.Equal(ExecLevels.Off, Assert.Single(machines!, m => m.AgentId == id).ExecLevel);
    }

    [Fact]
    public async Task The_profiles_os_version_is_cleaned_and_cut_to_100_characters()
    {
        var (_, agent) = await LoneAgentAsync("mm-osversion");
        var version = "macOS 15.1\u001b[31m\n" + new string('v', 200);
        (await agent.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(ExecLevels.Off, false, version, null, 2), TestUser.Json)).EnsureSuccessStatusCode();
        await using var db = api.Db();
        var machineId = (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agent.Tokens.AgentId)).MachineId;
        Assert.Equal(("macOS 15.1[31m" + new string('v', 200))[..100], (await db.Machines.AsNoTracking().SingleAsync(m => m.Id == machineId)).OsVersion);
    }

    [Fact]
    public async Task A_sample_is_stored_once_per_agent_and_time_and_a_repeat_changes_nothing()
    {
        var (_, agent) = await LoneAgentAsync("mm-idem");
        var at = Now.AddSeconds(-30);
        Assert.Equal(1, await StoredAsync(agent, Sample(at, cpu: 10)));
        Assert.Equal(0, await StoredAsync(agent, Sample(at, cpu: 99)));
        Assert.Equal(1, await StoredAsync(agent, Sample(at, cpu: 99), Sample(at.AddSeconds(1), cpu: 20)));
        var points = await agent.Http.GetFromJsonAsync<List<MetricSample>>($"/api/agent/machines/{agent.Tokens.AgentId}/metrics", TestUser.Json);
        Assert.Equal([10d, 20d], points!.Select(p => p.CpuPct).ToArray());
        Assert.Equal(0, await StoredAsync(agent));
    }

    [Fact]
    public async Task A_sample_outside_the_clock_skew_window_is_dropped_and_the_edges_are_kept()
    {
        var (_, agent) = await LoneAgentAsync("mm-skew");
        var kept = new[] { Now, Now.AddMinutes(2), Now.AddHours(-1).AddMinutes(-2) };
        var dropped = new[] { Now.AddMinutes(2).AddSeconds(1), Now.AddHours(1), Now.AddHours(-1).AddMinutes(-2).AddSeconds(-1), Now.AddDays(-3) };
        Assert.Equal(0, await StoredAsync(agent, dropped.Select(t => Sample(t)).ToArray()));
        Assert.Equal(3, await StoredAsync(agent, kept.Select(t => Sample(t)).ToArray()));
        await using var db = api.Db();
        var times = await db.MachineMetrics.AsNoTracking().Where(m => m.AgentId == agent.Tokens.AgentId).Select(m => m.SampledAt).ToListAsync();
        Assert.Equal(3, times.Count);
    }

    [Fact]
    public async Task A_report_stores_at_most_sixty_samples_and_refuses_impossible_numbers()
    {
        var (_, agent) = await LoneAgentAsync("mm-caps");
        var many = Enumerable.Range(0, 70).Select(i => Sample(Now.AddSeconds(-i))).ToArray();
        Assert.Equal(60, await StoredAsync(agent, many));
        Assert.Equal(0, await StoredAsync(agent, Sample(Now.AddMinutes(-30), used: -1), Sample(Now.AddMinutes(-31), total: -1)));
        Assert.Equal(2, await StoredAsync(agent, Sample(Now.AddMinutes(-32), cpu: 250), Sample(Now.AddMinutes(-33), cpu: -4)));
        var points = await agent.Http.GetFromJsonAsync<List<MetricSample>>($"/api/agent/machines/{agent.Tokens.AgentId}/metrics?minutes=60", TestUser.Json);
        Assert.All(points!, p => Assert.InRange(p.CpuPct, 0, 100));
        Assert.Contains(points!, p => p.CpuPct == 100);
        Assert.Contains(points!, p => p.CpuPct == 0);
    }

    [Fact]
    public async Task At_most_sixteen_disks_are_kept_and_unusable_ones_and_control_characters_are_removed()
    {
        var (_, agent) = await LoneAgentAsync("mm-disks");
        var disks = Enumerable.Range(0, 20).Select(i => new DiskSample("/mnt/d" + i, 10, 100)).ToList();
        Assert.Equal(1, await StoredAsync(agent, Sample(Now, disks: [.. disks])));
        var sample = Assert.Single((await agent.Http.GetFromJsonAsync<List<MetricSample>>($"/api/agent/machines/{agent.Tokens.AgentId}/metrics", TestUser.Json))!);
        Assert.Equal(16, sample.Disks.Count);
        Assert.Equal("/mnt/d15", sample.Disks[^1].Mount);

        Assert.Equal(1, await StoredAsync(agent, Sample(Now.AddSeconds(-5), disks:
            [new DiskSample("/ok\u001b[0m\nx", 5, 50), new DiskSample("/zero", 0, 0), new DiskSample("/neg", -1, 10), new DiskSample("/rtl‮", 1, 2)])));
        var cleaned = (await agent.Http.GetFromJsonAsync<List<MetricSample>>($"/api/agent/machines/{agent.Tokens.AgentId}/metrics", TestUser.Json))!
            .Single(s => s.SampledAt < Now.AddSeconds(-1)).Disks;
        Assert.Equal(["/ok[0mx", "/rtl"], cleaned.Select(d => d.Mount).ToArray());
    }

    [Fact]
    public async Task An_alert_opens_once_per_agent_kind_and_subject_keeps_its_peak_and_resolves()
    {
        var (_, agent) = await LoneAgentAsync("mm-alerts");
        var id = agent.Tokens.AgentId;
        Assert.Equal(HttpStatusCode.NoContent, (await Alert(agent, Report("cpu", "open", value: 91), Report("cpu", "open", value: 97),
            Report("cpu", "open", value: 93), Report("disk", "open", "/"), Report("disk", "open", "/data"), Report("memory", "open"))).StatusCode);
        var open = (await agent.Http.GetFromJsonAsync<List<AlertView>>("/api/agent/alerts", TestUser.Json))!;
        Assert.Equal(4, open.Count(a => a.AgentId == id));
        var cpu = open.Single(a => a.Kind == "cpu");
        Assert.Equal((97d, 93d), (cpu.PeakValue, cpu.LastValue));
        Assert.Equal(new[] { "/", "/data" }, open.Where(a => a.Kind == "disk").Select(a => a.Subject).Order().ToArray());
        Assert.All(open.Where(a => a.AgentId == id), a => Assert.Equal(AlertStates.Open, a.State));

        await Alert(agent, Report("cpu", "resolved", value: 40), Report("disk", "resolved", "/data"), Report("disk", "resolved", "/missing"));
        Assert.Equal(2, (await agent.Http.GetFromJsonAsync<List<AlertView>>("/api/agent/alerts", TestUser.Json))!.Count(a => a.AgentId == id && a.State == "open"));
        var all = await agent.Http.GetFromJsonAsync<List<AlertView>>("/api/agent/alerts?resolved=true", TestUser.Json);
        var resolved = all!.Single(a => a.Kind == "cpu");
        Assert.Equal((AlertStates.Resolved, 40d), (resolved.State, resolved.LastValue));
        Assert.NotNull(resolved.ResolvedAt);

        await Alert(agent, Report("cpu", "open", value: 92));
        Assert.Equal(2, (await agent.Http.GetFromJsonAsync<List<AlertView>>("/api/agent/alerts?resolved=true", TestUser.Json))!.Count(a => a.Kind == "cpu"));
    }

    [Fact]
    public async Task An_agent_may_not_report_offline_and_unknown_kinds_and_states_are_ignored()
    {
        var (_, agent) = await LoneAgentAsync("mm-offline-kind");
        await Alert(agent, Report("offline", "open"), Report("bogus", "open"), Report("cpu", "paused"), Report("cpu", "open", value: 80));
        var alerts = (await agent.Http.GetFromJsonAsync<List<AlertView>>("/api/agent/alerts?resolved=true", TestUser.Json))!.Where(a => a.AgentId == agent.Tokens.AgentId).ToList();
        Assert.Equal("cpu", Assert.Single(alerts).Kind);
    }

    [Fact]
    public async Task An_alert_subject_is_cleaned_and_cut()
    {
        var (_, agent) = await LoneAgentAsync("mm-subject");
        await Alert(agent, Report("disk", "open", "/data\u001b[1m\n" + new string('s', 300)));
        var alert = Assert.Single((await agent.Http.GetFromJsonAsync<List<AlertView>>("/api/agent/alerts", TestUser.Json))!, a => a.AgentId == agent.Tokens.AgentId);
        Assert.Equal(("/data[1m" + new string('s', 300))[..200], alert.Subject);
    }

    [Fact]
    public async Task The_machine_list_puts_service_agents_first_with_the_latest_sample_and_the_open_alerts()
    {
        var admin = await api.NewClient().SignedUpAsync("mm-list");
        var plain = await admin.ConnectAgentAsync();
        var service = await admin.ConnectAgentAsync();
        await RemoteKit.ProfileAsync(plain, ExecLevels.Argv);
        await RemoteKit.ProfileAsync(service, ExecLevels.Off, service: true);
        await StoredAsync(service, Sample(Now.AddMinutes(-3), cpu: 30), Sample(Now.AddMinutes(-1), cpu: 55, used: 600));
        await Alert(service, Report("cpu", "open"), Report("disk", "open", "/"));
        await StoredAsync(plain, Sample(Now.AddHours(-1).AddMinutes(-1), cpu: 10));

        var list = (await plain.Http.GetFromJsonAsync<List<MachineView>>("/api/agent/machines", TestUser.Json))!;
        Assert.Equal([service.Tokens.AgentId, plain.Tokens.AgentId], list.Select(m => m.AgentId).ToArray());
        var first = list[0];
        Assert.Equal((true, 2, 55d, 600L), (first.ServiceMode, first.OpenAlerts, first.Latest!.CpuPct, first.Latest.MemUsedBytes));
        Assert.Equal((false, 0, "macos", "argv"), (list[1].ServiceMode, list[1].OpenAlerts, list[1].Os, list[1].ExecLevel));
        Assert.Null(list[1].Latest); // a sample older than an hour is not "latest"
        Assert.Equal("Test Person", first.UserName);
    }

    [Fact]
    public async Task A_machine_is_online_for_three_minutes_after_its_last_heartbeat_and_then_not()
    {
        var (_, agent) = await LoneAgentAsync("mm-online");
        MachineView Mine(List<MachineView> l) => l.Single(m => m.AgentId == agent.Tokens.AgentId);
        (await agent.Http.PostAsync("/api/agent/heartbeat", null)).EnsureSuccessStatusCode();
        var seen = Mine((await agent.Http.GetFromJsonAsync<List<MachineView>>("/api/agent/machines", TestUser.Json))!);
        Assert.True(seen.Online);
        Assert.NotNull(seen.LastSeenAt);
        api.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(Mine((await agent.Http.GetFromJsonAsync<List<MachineView>>("/api/agent/machines", TestUser.Json))!).Online);
    }

    [Fact]
    public async Task A_viewers_agent_reports_but_reads_no_other_machine()
    {
        var team = await RemoteKit.TeamAsync(api);
        var viewer = await RemoteKit.MemberAsync(api, team.Admin, "mm-viewer");
        var viewerAgent = await viewer.ConnectAgentAsync(team.WorkspaceId);
        (await team.Admin.SendAsync(HttpMethod.Patch, $"/api/workspaces/{team.WorkspaceId}/members/{viewer.Id}", new { role = "viewer" })).EnsureSuccessStatusCode();
        await RemoteKit.ProfileAsync(viewerAgent, ExecLevels.Argv);
        Assert.Equal(1, await StoredAsync(viewerAgent, Sample(Now)));
        Assert.Equal(HttpStatusCode.NoContent, (await Alert(viewerAgent, Report("cpu", "open"))).StatusCode);
        foreach (var url in new[] { "/api/agent/machines", $"/api/agent/machines/{team.TargetId}/metrics", "/api/agent/alerts" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await viewerAgent.Http.GetAsync(url)).StatusCode);
        }
    }

    [Fact]
    public async Task The_web_reads_machines_alerts_and_metrics_for_members_of_any_role_and_not_for_outsiders()
    {
        var team = await RemoteKit.TeamAsync(api);
        await StoredAsync(team.Target, Sample(Now, cpu: 42));
        await Alert(team.Target, Report("memory", "open", value: 93));
        var viewer = await RemoteKit.MemberAsync(api, team.Admin, "mm-web-viewer", "viewer");
        var outsider = await api.NewClient().SignedUpAsync("mm-web-outsider");
        var ws = team.WorkspaceId;

        foreach (var reader in new[] { team.Admin, team.Other, viewer })
        {
            var machines = await reader.GetJsonAsync($"/api/workspaces/{ws}/machines");
            Assert.Equal(1, machines.EnumerateArray().Single(m => m.GetProperty("agentId").GetGuid() == team.TargetId).GetProperty("openAlerts").GetInt32());
            var alerts = await reader.GetJsonAsync($"/api/workspaces/{ws}/alerts?agent={team.TargetId}");
            Assert.Equal("memory", Assert.Single(alerts.EnumerateArray()).GetProperty("kind").GetString());
            var points = await reader.GetJsonAsync($"/api/agents/{team.TargetId}/metrics");
            Assert.Equal(42d, Assert.Single(points.EnumerateArray()).GetProperty("cpuPct").GetDouble());
        }

        Assert.Empty((await team.Admin.GetJsonAsync($"/api/workspaces/{ws}/alerts?agent={team.RequesterId}")).EnumerateArray());
        foreach (var url in new[] { $"/api/workspaces/{ws}/machines", $"/api/workspaces/{ws}/alerts", $"/api/agents/{team.TargetId}/metrics", $"/api/agents/{Guid.NewGuid()}/metrics" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.Http.GetAsync(url)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await team.Admin.Http.GetAsync($"/api/agents/{Guid.NewGuid()}/metrics")).StatusCode);
    }
}
