using System.Net;
using System.Net.Http.Json;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The workspace's remote switch and alert thresholds, and the chores that keep remote data tidy (ADR-0004).</summary>
[Collection(ApiGroup.Name)]
public sealed class RemoteSettingsTests(ApiFactory api)
{
    private DateTimeOffset Now => api.Clock.GetUtcNow();

    private static Task<HttpResponseMessage> Put(TestUser user, object body) =>
        user.SendAsync(HttpMethod.Put, $"/api/workspaces/{user.WorkspaceId}/remote-settings", body);

    [Fact]
    public async Task A_new_workspace_has_the_switch_off_and_default_thresholds_that_agents_also_read()
    {
        var admin = await api.NewClient().SignedUpAsync("rs-defaults");
        var settings = await admin.GetJsonAsync($"/api/workspaces/{admin.WorkspaceId}/remote-settings");
        Assert.Equal((false, 90, 90, 90, 300),
            (settings.GetProperty("remoteRunsEnabled").GetBoolean(), settings.GetProperty("alertCpuPct").GetInt32(),
                settings.GetProperty("alertMemoryPct").GetInt32(), settings.GetProperty("alertDiskPct").GetInt32(),
                settings.GetProperty("alertSustainSeconds").GetInt32()));

        var agent = await admin.ConnectAgentAsync();
        var seen = await agent.Http.GetFromJsonAsync<AgentSettings>("/api/agent/settings", TestUser.Json);
        Assert.False(seen!.RemoteRuns);
        Assert.Equal(new AlertThresholds(90, 90, 90, 300), seen.Alerts);
        (await Put(admin, new { remoteRunsEnabled = true, alertCpuPct = 75, alertMemoryPct = 80, alertDiskPct = 85, alertSustainSeconds = 600 })).EnsureSuccessStatusCode();
        seen = await agent.Http.GetFromJsonAsync<AgentSettings>("/api/agent/settings", TestUser.Json);
        Assert.True(seen!.RemoteRuns);
        Assert.Equal(new AlertThresholds(75, 80, 85, 600), seen.Alerts);
    }

    [Fact]
    public async Task Only_an_admin_changes_the_settings_and_any_member_reads_them()
    {
        var team = await RemoteKit.TeamAsync(api, enabled: false);
        var viewer = await RemoteKit.MemberAsync(api, team.Admin, "rs-viewer", "viewer");
        var outsider = await api.NewClient().SignedUpAsync("rs-outsider");
        var url = $"/api/workspaces/{team.WorkspaceId}/remote-settings";
        foreach (var notAdmin in new[] { team.Owner, viewer, outsider })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await notAdmin.SendAsync(HttpMethod.Put, url, new { remoteRunsEnabled = true })).StatusCode);
        }

        Assert.False((await team.Admin.GetJsonAsync(url)).GetProperty("remoteRunsEnabled").GetBoolean());
        foreach (var reader in new[] { team.Owner, viewer })
        {
            Assert.Equal(HttpStatusCode.OK, (await reader.Http.GetAsync(url)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Http.GetAsync(url)).StatusCode);
        var admin = await RemoteKit.MemberAsync(api, team.Admin, "rs-admin", "admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(HttpMethod.Put, url, new { remoteRunsEnabled = true })).StatusCode);
        Assert.True((await team.Admin.GetJsonAsync(url)).GetProperty("remoteRunsEnabled").GetBoolean());
    }

    [Theory]
    [InlineData("alertCpuPct", 0)]
    [InlineData("alertCpuPct", 101)]
    [InlineData("alertMemoryPct", 0)]
    [InlineData("alertMemoryPct", 101)]
    [InlineData("alertDiskPct", -1)]
    [InlineData("alertDiskPct", 101)]
    [InlineData("alertSustainSeconds", 59)]
    [InlineData("alertSustainSeconds", 86401)]
    public async Task A_threshold_outside_its_range_is_refused_and_changes_nothing(string field, int value)
    {
        var admin = await api.NewClient().SignedUpAsync("rs-range");
        var response = await admin.SendAsync(HttpMethod.Put, $"/api/workspaces/{admin.WorkspaceId}/remote-settings",
            new Dictionary<string, object> { [field] = value, ["remoteRunsEnabled"] = true });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await admin.GetJsonAsync($"/api/workspaces/{admin.WorkspaceId}/remote-settings")).GetProperty("remoteRunsEnabled").GetBoolean());
    }

    [Fact]
    public async Task The_edges_of_every_range_are_accepted_and_a_partial_update_keeps_the_rest()
    {
        var admin = await api.NewClient().SignedUpAsync("rs-edges");
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, new { alertCpuPct = 1, alertMemoryPct = 100, alertDiskPct = 1, alertSustainSeconds = 60 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, new { alertSustainSeconds = 86400 })).StatusCode);
        var settings = await admin.GetJsonAsync($"/api/workspaces/{admin.WorkspaceId}/remote-settings");
        Assert.Equal((1, 100, 1, 86400, false),
            (settings.GetProperty("alertCpuPct").GetInt32(), settings.GetProperty("alertMemoryPct").GetInt32(),
                settings.GetProperty("alertDiskPct").GetInt32(), settings.GetProperty("alertSustainSeconds").GetInt32(),
                settings.GetProperty("remoteRunsEnabled").GetBoolean()));
        await using var db = api.Db();
        var audit = await db.AuditEvents.AsNoTracking().Where(a => a.Action == "remote.settings_changed" && a.WorkspaceId == admin.WorkspaceId).ToListAsync();
        Assert.Equal(2, audit.Count);
        Assert.All(audit, a => Assert.Equal(admin.Id, a.ActorUserId));
    }

    [Fact]
    public async Task Turning_the_switch_off_cancels_every_open_run_of_the_workspace_and_tells_the_target()
    {
        var team = await RemoteKit.TeamAsync(api);
        var spare = await team.Owner.ConnectAgentAsync(team.WorkspaceId);
        await RemoteKit.ProfileAsync(spare, ExecLevels.Argv);
        var pending = (await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()))).Id;
        var approvedReq = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey());
        var approved = (await RemoteKit.CreateAsync(team.Requester, approvedReq)).Id;
        (await RemoteKit.ApproveAsync(team.Owner, approved, approvedReq)).EnsureSuccessStatusCode();
        using var stream = await RemoteKit.OpenAsync(spare.Http, "/api/agent/stream");
        await RemoteKit.NextEventAsync(stream, AgentStreamEvents.Ready);
        var runningReq = RemoteKit.Argv(spare.Tokens.AgentId, RemoteKit.NewKey());
        var running = (await RemoteKit.CreateAsync(team.Requester, runningReq)).Id;
        (await RemoteKit.ApproveAsync(team.Owner, running, runningReq)).EnsureSuccessStatusCode();
        await RemoteKit.RunEventAsync(stream, AgentStreamEvents.Run, running);
        (await RemoteKit.StatusAsync(spare, running, new RunStatusUpdate("running"))).EnsureSuccessStatusCode();
        var deniedReq = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey());
        var denied = (await RemoteKit.CreateAsync(team.Requester, deniedReq)).Id;
        (await team.Owner.PostAsync($"/api/runs/{denied}/deny", new { })).EnsureSuccessStatusCode();

        (await Put(team.Admin, new { remoteRunsEnabled = false })).EnsureSuccessStatusCode();

        Assert.Equal(RunStatuses.Cancelled, await RemoteKit.StatusOfAsync(api, pending));
        Assert.Equal(RunStatuses.Cancelled, await RemoteKit.StatusOfAsync(api, approved));
        Assert.Equal(RunStatuses.Cancelled, await RemoteKit.StatusOfAsync(api, running));
        Assert.Equal(RunStatuses.Denied, await RemoteKit.StatusOfAsync(api, denied));
        Assert.Equal(running, (await RemoteKit.NextEventAsync(stream, AgentStreamEvents.RunCancel)).GetProperty("id").GetGuid());
        await RemoteKit.AssertProblemAsync(await RemoteKit.PostRunAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey())),
            HttpStatusCode.Conflict, RemoteErrors.Disabled);
        (await Put(team.Admin, new { remoteRunsEnabled = true })).EnsureSuccessStatusCode();
        Assert.Equal(RunStatuses.Cancelled, await RemoteKit.StatusOfAsync(api, pending));
    }

    [Fact]
    public async Task Another_workspaces_runs_are_not_cancelled_by_this_workspaces_switch()
    {
        var mine = await RemoteKit.TeamAsync(api);
        var theirs = await RemoteKit.TeamAsync(api);
        var theirRun = (await RemoteKit.CreateAsync(theirs.Requester, RemoteKit.Argv(theirs.TargetId, RemoteKit.NewKey()))).Id;
        (await Put(mine.Admin, new { remoteRunsEnabled = false })).EnsureSuccessStatusCode();
        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, theirRun));
    }

    [Fact]
    public async Task The_housekeeper_opens_one_offline_alert_for_a_silent_service_agent_and_resolves_it_after_a_heartbeat()
    {
        var admin = await api.NewClient().SignedUpAsync("rs-offline");
        var service = await admin.ConnectAgentAsync();
        var plain = await admin.ConnectAgentAsync();
        await RemoteKit.ProfileAsync(service, ExecLevels.Off, service: true);
        await RemoteKit.ProfileAsync(plain, ExecLevels.Off);
        (await service.Http.PostAsync("/api/agent/heartbeat", null)).EnsureSuccessStatusCode();
        (await plain.Http.PostAsync("/api/agent/heartbeat", null)).EnsureSuccessStatusCode();
        await RemoteKit.HousekeepAsync(api);
        Assert.Empty(await OfflineAlertsAsync(service.Tokens.AgentId));

        api.Clock.Advance(TimeSpan.FromMinutes(6));
        await RemoteKit.HousekeepAsync(api);
        await RemoteKit.HousekeepAsync(api);
        var alert = Assert.Single(await OfflineAlertsAsync(service.Tokens.AgentId));
        Assert.Equal((AlertStates.Open, ""), (alert.State, alert.Subject));
        Assert.Equal(5d, alert.ThresholdPct);
        Assert.Empty(await OfflineAlertsAsync(plain.Tokens.AgentId));
        var visible = await admin.GetJsonAsync($"/api/workspaces/{admin.WorkspaceId}/alerts");
        Assert.Contains(visible.EnumerateArray(), a => a.GetProperty("kind").GetString() == "offline");

        (await service.Http.PostAsync("/api/agent/heartbeat", null)).EnsureSuccessStatusCode();
        await RemoteKit.HousekeepAsync(api);
        var resolved = Assert.Single(await OfflineAlertsAsync(service.Tokens.AgentId));
        Assert.Equal(AlertStates.Resolved, resolved.State);
        Assert.NotNull(resolved.ResolvedAt);
    }

    private async Task<List<MachineAlert>> OfflineAlertsAsync(Guid agentId)
    {
        await using var db = api.Db();
        return await db.MachineAlerts.AsNoTracking().Where(a => a.AgentId == agentId && a.Kind == AlertKinds.Offline).ToListAsync();
    }

    [Fact]
    public async Task Metrics_older_than_seven_days_are_deleted_and_newer_ones_stay()
    {
        var admin = await api.NewClient().SignedUpAsync("rs-metrics");
        var agent = await admin.ConnectAgentAsync();
        (await agent.Http.PostAsJsonAsync("/api/agent/metrics", new MetricsReport([new MetricSample(Now.AddMinutes(-1), 10, 1, 2, [])]), TestUser.Json)).EnsureSuccessStatusCode();
        api.Clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(5));
        var fresh = await admin.ConnectAgentAsync(); // the first agent's access token ran out with the days
        (await fresh.Http.PostAsJsonAsync("/api/agent/metrics", new MetricsReport([new MetricSample(Now, 20, 1, 2, [])]), TestUser.Json)).EnsureSuccessStatusCode();

        await RemoteKit.HousekeepAsync(api);
        await using var check = api.Db();
        Assert.Empty(await check.MachineMetrics.AsNoTracking().Where(m => m.AgentId == agent.Tokens.AgentId).ToListAsync());
        Assert.Equal(20d, Assert.Single(await check.MachineMetrics.AsNoTracking().Where(m => m.AgentId == fresh.Tokens.AgentId).ToListAsync()).CpuPct);
    }

    [Fact]
    public async Task Finished_runs_with_their_output_and_resolved_alerts_leave_after_the_retention_and_open_ones_stay()
    {
        var team = await RemoteKit.TeamAsync(api);
        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/workspaces/{team.WorkspaceId}/settings", new { retentionDays = 1 })).EnsureSuccessStatusCode();
        var finished = (await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()))).Id;
        (await team.Admin.PostAsync($"/api/runs/{finished}/cancel")).EnsureSuccessStatusCode();
        await using (var db = api.Db())
        {
            db.RemoteRunOutput.Add(new RemoteRunOutput { RunId = finished, Seq = 0, Stream = "stdout", Body = "old", Bytes = 3, ReceivedAt = Now });
            await db.SaveChangesAsync();
        }

        var at = Now;
        (await team.Target.Http.PostAsJsonAsync("/api/agent/alerts",
            new[] { new AlertReport("cpu", "", "open", 95, 90, at), new AlertReport("disk", "/", "open", 95, 90, at), new AlertReport("cpu", "", "resolved", 10, 90, at) },
            TestUser.Json)).EnsureSuccessStatusCode();

        api.Clock.Advance(TimeSpan.FromDays(2));
        var newAgent = await team.Admin.ConnectAgentAsync();
        var recent = (await RemoteKit.CreateAsync(newAgent, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()))).Id;
        await RemoteKit.HousekeepAsync(api);

        await using var check = api.Db();
        Assert.False(await check.RemoteRuns.AnyAsync(r => r.Id == finished));
        Assert.False(await check.RemoteRunOutput.AnyAsync(o => o.RunId == finished));
        Assert.True(await check.RemoteRuns.AnyAsync(r => r.Id == recent));
        var alerts = await check.MachineAlerts.AsNoTracking().Where(a => a.AgentId == team.TargetId).ToListAsync();
        Assert.Equal(["disk"], alerts.Select(a => a.Kind).ToArray());
        Assert.Equal(AlertStates.Open, alerts[0].State);
    }
}
