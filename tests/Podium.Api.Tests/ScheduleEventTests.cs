using System.Net;
using System.Net.Http.Json;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Remote;
using Podium.Api.Streaming;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>The workspace stream hears of a schedule the engine stopped, as it does of one a person paused or ended (ADR-0010 §3).</summary>
[Collection(ApiGroup.Name)]
public sealed class ScheduleEventTests(ApiFactory api)
{
    private static readonly string[] Suite = ["/opt/ci/run-tests", "--suite", "unit"];

    private static async Task<(RemoteTeam Team, Guid JobId, Guid ScheduleId)> ScheduledAsync(ApiFactory api)
    {
        var team = await RemoteKit.TeamAsync(api);
        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/workspaces/{team.WorkspaceId}/remote-settings", new { remoteSchedulesEnabled = true })).EnsureSuccessStatusCode();
        var proposed = await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs", new JobProposal(team.TargetId, "Run tests", Suite, "/opt/ci", 300, "needs a test run"), TestUser.Json);
        Assert.Equal(HttpStatusCode.OK, proposed.StatusCode);
        var job = (await proposed.Content.ReadFromJsonAsync<JobView>(TestUser.Json))!;
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/jobs/{job.Id}/approve", new { })).StatusCode);
        var created = await team.Owner.PostAsync("/api/schedules", new { jobId = job.Id, kind = "interval", intervalMinutes = 15 });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        return (team, job.Id, (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid());
    }

    private async Task FireAsync()
    {
        await using var scope = api.Services.CreateAsyncScope();
        await JobSchedules.FireDueAsync(scope.ServiceProvider.GetRequiredService<PodiumDb>(), api.Services.GetRequiredService<ApiConfig>(),
            api.Services.GetRequiredService<Broker>(), api.Clock.GetUtcNow(), CancellationToken.None);
    }

    [Fact]
    public async Task A_schedule_the_engine_pauses_is_published_to_the_workspace()
    {
        var (team, _, id) = await ScheduledAsync(api);
        using var web = await RemoteKit.OpenAsync(team.Admin.Http, $"/api/workspaces/{team.WorkspaceId}/stream");
        await RemoteKit.NextEventAsync(web, "ready");
        await RemoteKit.ProfileAsync(team.Target, ExecLevels.Off);
        api.Clock.Advance(TimeSpan.FromMinutes(16));

        await FireAsync();

        var message = await RemoteKit.NextEventAsync(web, JobSchedules.WorkspaceEvent, e => e.GetProperty("id").GetGuid() == id);
        Assert.Equal(team.TargetId, message.GetProperty("targetAgentId").GetGuid());
    }

    [Fact]
    public async Task A_schedule_ended_because_its_job_was_retired_is_published_to_the_workspace()
    {
        var (team, jobId, id) = await ScheduledAsync(api);
        using var web = await RemoteKit.OpenAsync(team.Admin.Http, $"/api/workspaces/{team.WorkspaceId}/stream");
        await RemoteKit.NextEventAsync(web, "ready");

        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/jobs/{jobId}/retire")).StatusCode);

        await RemoteKit.NextEventAsync(web, JobSchedules.WorkspaceEvent, e => e.GetProperty("id").GetGuid() == id);
    }
}
