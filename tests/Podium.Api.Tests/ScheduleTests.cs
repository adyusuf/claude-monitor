using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Background;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Remote;
using Podium.Api.Streaming;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>The unit rules of a schedule's timing (ADR-0010 §3).</summary>
public sealed class ScheduleLimitsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 16, 10, 7, 33, TimeSpan.Zero);

    [Theory]
    [InlineData(ScheduleKinds.Interval, 15, null, true)]
    [InlineData(ScheduleKinds.Interval, 10080, null, true)]
    [InlineData(ScheduleKinds.Interval, 14, null, false)]
    [InlineData(ScheduleKinds.Interval, 10081, null, false)]
    [InlineData(ScheduleKinds.Interval, null, null, false)]
    [InlineData(ScheduleKinds.Interval, 60, 5, false)]
    [InlineData(ScheduleKinds.Daily, null, 0, true)]
    [InlineData(ScheduleKinds.Daily, null, 1439, true)]
    [InlineData(ScheduleKinds.Daily, null, 1440, false)]
    [InlineData(ScheduleKinds.Daily, null, -1, false)]
    [InlineData(ScheduleKinds.Daily, 60, 5, false)]
    [InlineData("cron", 60, null, false)]
    [InlineData(null, 60, null, false)]
    public void Only_an_interval_of_15_minutes_to_7_days_or_a_time_of_day_is_a_schedule(string? kind, int? interval, int? daily, bool valid) =>
        Assert.Equal(valid, ScheduleLimits.Valid(kind, interval, daily));

    [Fact]
    public void An_interval_counts_from_the_minute_it_is_asked_at_and_a_daily_time_is_the_next_one_after_it()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 11, 7, 0, TimeSpan.Zero), ScheduleLimits.Next(ScheduleKinds.Interval, 60, null, At));
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero), ScheduleLimits.Next(ScheduleKinds.Daily, null, 12 * 60, At)); // later today
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 3, 0, 0, TimeSpan.Zero), ScheduleLimits.Next(ScheduleKinds.Daily, null, 3 * 60, At)); // already passed: tomorrow
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 10, 7, 0, TimeSpan.Zero), ScheduleLimits.Next(ScheduleKinds.Daily, null, 10 * 60 + 7, At)); // this very minute has begun
        Assert.Null(ScheduleLimits.Next(ScheduleKinds.Interval, 5, null, At));
    }
}

[Collection(ApiGroup.Name)]
public sealed class ScheduleTests(ApiFactory api)
{
    private static readonly string[] Suite = ["/opt/ci/run-tests", "--suite", "unit"];

    private static async Task<RemoteTeam> TeamAsync(ApiFactory api, bool schedules = true, string execLevel = ExecLevels.Argv)
    {
        var team = await RemoteKit.TeamAsync(api, execLevel);
        if (schedules) await SetSchedulesAsync(team, true);
        return team;
    }

    private static async Task SetSchedulesAsync(RemoteTeam team, bool on) =>
        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/workspaces/{team.Admin.WorkspaceId}/remote-settings", new { remoteSchedulesEnabled = on })).EnsureSuccessStatusCode();

    private static async Task<JobView> ActiveJobAsync(RemoteTeam team, string[]? argv = null, string name = "Run tests")
    {
        var response = await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs", new JobProposal(team.TargetId, name, argv ?? Suite, "/opt/ci", 300, "needs a test run"), TestUser.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<JobView>(TestUser.Json))!;
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/jobs/{job.Id}/approve", new { })).StatusCode);
        return job;
    }

    private static async Task<JsonElement> CreateAsync(RemoteTeam team, Guid jobId, int minutes = 60)
    {
        var response = await team.Owner.PostAsync("/api/schedules", new { jobId, kind = "interval", intervalMinutes = minutes });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JobSchedule> RowAsync(Guid id)
    {
        await using var db = api.Db();
        return await db.JobSchedules.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    private async Task<List<RemoteRun>> RunsOfAsync(Guid scheduleId)
    {
        await using var db = api.Db();
        return await db.RemoteRuns.AsNoTracking().Where(r => r.ScheduleId == scheduleId).OrderBy(r => r.CreatedAt).ToListAsync();
    }

    private async Task SetRunStatusAsync(Guid runId, string status)
    {
        await using var db = api.Db();
        await db.RemoteRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status));
    }

    private async Task FireAsync()
    {
        await using var scope = api.Services.CreateAsyncScope();
        await JobSchedules.FireDueAsync(scope.ServiceProvider.GetRequiredService<PodiumDb>(), api.Services.GetRequiredService<ApiConfig>(),
            api.Services.GetRequiredService<Broker>(), api.Clock.GetUtcNow(), CancellationToken.None);
    }

    // ---- creating -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_owner_starts_a_schedule_for_an_active_job_and_everyone_in_the_workspace_can_read_it()
    {
        var team = await TeamAsync(api);
        var job = await ActiveJobAsync(team);
        var created = await CreateAsync(team, job.Id, 60);

        Assert.Equal((ScheduleStatuses.Active, "interval", 60, true, "Run tests"), (created.GetProperty("status").GetString(), created.GetProperty("kind").GetString(),
            created.GetProperty("intervalMinutes").GetInt32(), created.GetProperty("isOwner").GetBoolean(), created.GetProperty("jobName").GetString()));
        Assert.True(created.GetProperty("nextRunAt").GetDateTimeOffset() > api.Clock.GetUtcNow());

        var seenByOther = await team.Other.GetJsonAsync($"/api/agents/{team.TargetId}/schedules");
        Assert.Equal(1, seenByOther.GetArrayLength());
        Assert.False(seenByOther[0].GetProperty("isOwner").GetBoolean());
    }

    [Fact]
    public async Task A_daily_schedule_is_accepted_and_a_shape_that_is_not_a_schedule_is_refused()
    {
        var team = await TeamAsync(api);
        var job = await ActiveJobAsync(team);
        Assert.Equal(HttpStatusCode.OK, (await team.Owner.PostAsync("/api/schedules", new { jobId = job.Id, kind = "daily", dailyAtMinutes = 180 })).StatusCode);
        foreach (var bad in new object[] { new { jobId = job.Id, kind = "interval", intervalMinutes = 5 }, new { jobId = job.Id, kind = "daily", dailyAtMinutes = 1440 },
                     new { jobId = job.Id, kind = "cron", intervalMinutes = 60 }, new { jobId = job.Id, kind = "interval", intervalMinutes = 60, dailyAtMinutes = 5 } })
        {
            await RemoteKit.AssertInvalidAsync(await team.Owner.PostAsync("/api/schedules", bad), "kind", RemoteErrors.InvalidSchedule);
        }
    }

    [Fact]
    public async Task Nothing_is_scheduled_while_the_workspace_switch_is_off_and_only_the_jobs_owner_may_schedule_it()
    {
        var team = await TeamAsync(api, schedules: false);
        var job = await ActiveJobAsync(team);
        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync("/api/schedules", new { jobId = job.Id, kind = "interval", intervalMinutes = 60 }), HttpStatusCode.Conflict, RemoteErrors.SchedulesDisabled);
        await SetSchedulesAsync(team, true);
        Assert.Equal(HttpStatusCode.NotFound, (await team.Other.PostAsync("/api/schedules", new { jobId = job.Id, kind = "interval", intervalMinutes = 60 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await team.Admin.PostAsync("/api/schedules", new { jobId = job.Id, kind = "interval", intervalMinutes = 60 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await team.Owner.PostAsync("/api/schedules", new { jobId = job.Id, kind = "interval", intervalMinutes = 60 })).StatusCode);
    }

    [Fact]
    public async Task A_job_with_a_placeholder_a_machine_that_cannot_run_commands_and_a_sixth_schedule_are_refused()
    {
        var team = await TeamAsync(api);
        var placeholder = await ActiveJobAsync(team, ["/opt/ci/run-tests", "--ref", "{word}"], "By ref");
        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync("/api/schedules", new { jobId = placeholder.Id, kind = "interval", intervalMinutes = 60 }), HttpStatusCode.Conflict, RemoteErrors.SchedulePlaceholder);

        var job = await ActiveJobAsync(team);
        for (var i = 0; i < ScheduleLimits.PerTargetMax; i++) await CreateAsync(team, job.Id, 60 + i);
        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync("/api/schedules", new { jobId = job.Id, kind = "interval", intervalMinutes = 90 }), HttpStatusCode.Conflict, RemoteErrors.ScheduleLimit);
    }

    [Theory]
    [InlineData("production", "application")]
    [InlineData("test", "database")]
    public async Task A_production_or_database_machine_takes_no_schedule(string environment, string function)
    {
        var team = await TeamAsync(api, execLevel: ExecLevels.Shell);
        await ScriptKit.ProfileAsync(team, ExecLevels.Shell, [ScriptKit.Sh]);
        var job = await ActiveJobAsync(team); // approved while the machine is not strict (on a strict one the approval itself needs a code)
        await ScriptKit.SetRoleAsync(team, environment, function);
        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync("/api/schedules", new { jobId = job.Id, kind = "interval", intervalMinutes = 60 }), HttpStatusCode.Conflict, RemoteErrors.ScheduleStrict);
    }

    [Fact]
    public async Task An_old_sign_in_needs_the_two_step_code_like_a_grant_does()
    {
        var team = await TeamAsync(api);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        var job = await ActiveJobAsync(team);
        api.Clock.Advance(TimeSpan.FromMinutes(11));
        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync("/api/schedules", new { jobId = job.Id, kind = "interval", intervalMinutes = 60 }), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        Assert.Equal(HttpStatusCode.OK, (await team.Owner.PostAsync("/api/schedules", new { jobId = job.Id, kind = "interval", intervalMinutes = 60, code = RemoteKit.TotpNow(api, secret) })).StatusCode);
    }

    // ---- firing ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_due_schedule_creates_one_approved_run_of_its_job_and_counts_the_next_tick_from_now()
    {
        var team = await TeamAsync(api);
        var job = await ActiveJobAsync(team);
        var id = (await CreateAsync(team, job.Id, 60)).GetProperty("id").GetGuid();
        await FireAsync();
        Assert.Empty(await RunsOfAsync(id)); // not due yet

        api.Clock.Advance(TimeSpan.FromMinutes(61));
        await FireAsync();
        await FireAsync(); // the same moment again fires nothing new

        var run = Assert.Single(await RunsOfAsync(id));
        Assert.Equal((RunStatuses.Approved, RunModes.Argv, job.Id, team.TargetId), (run.Status, run.Mode, run.JobId, run.TargetAgentId));
        Assert.Equal(Suite, run.Argv);
        Assert.Equal(team.Owner.Id, run.DecidedBy);
        var row = await RowAsync(id);
        Assert.Equal(run.Id, row.LastRunId);
        Assert.False(row.LastRunSettled);
        Assert.True(row.NextRunAt > api.Clock.GetUtcNow());
    }

    [Fact]
    public async Task Downtime_is_not_made_up_a_long_gap_fires_one_run_not_one_per_missed_tick()
    {
        var team = await TeamAsync(api);
        var job = await ActiveJobAsync(team);
        var id = (await CreateAsync(team, job.Id, 15)).GetProperty("id").GetGuid();
        api.Clock.Advance(TimeSpan.FromHours(10));
        await FireAsync();
        await FireAsync();
        Assert.Single(await RunsOfAsync(id));
        Assert.True((await RowAsync(id)).NextRunAt >= api.Clock.GetUtcNow().AddMinutes(14));
    }

    [Fact]
    public async Task A_schedule_does_not_overlap_its_own_last_run_it_skips_that_tick_and_goes_on_when_the_run_is_over()
    {
        var team = await TeamAsync(api);
        var job = await ActiveJobAsync(team);
        var id = (await CreateAsync(team, job.Id, 15)).GetProperty("id").GetGuid();
        api.Clock.Advance(TimeSpan.FromMinutes(16));
        await FireAsync();
        var first = Assert.Single(await RunsOfAsync(id));

        api.Clock.Advance(TimeSpan.FromMinutes(16));
        await FireAsync(); // the first run is still approved, not finished
        Assert.Single(await RunsOfAsync(id));

        await SetRunStatusAsync(first.Id, RunStatuses.Succeeded);
        api.Clock.Advance(TimeSpan.FromMinutes(16));
        await FireAsync();
        Assert.Equal(2, (await RunsOfAsync(id)).Count);
    }

    [Fact]
    public async Task Two_instances_firing_at_once_fire_one_tick_once()
    {
        var team = await TeamAsync(api);
        var job = await ActiveJobAsync(team);
        var id = (await CreateAsync(team, job.Id, 15)).GetProperty("id").GetGuid();
        api.Clock.Advance(TimeSpan.FromMinutes(16));
        await Task.WhenAll(FireAsync(), FireAsync(), FireAsync());
        Assert.Single(await RunsOfAsync(id));
    }

    [Fact]
    public async Task Three_failed_runs_in_a_row_pause_the_schedule_and_a_success_in_between_starts_the_count_over()
    {
        var team = await TeamAsync(api);
        var job = await ActiveJobAsync(team);
        var id = (await CreateAsync(team, job.Id, 15)).GetProperty("id").GetGuid();

        async Task<RemoteRun> TickAsync(string outcome)
        {
            api.Clock.Advance(TimeSpan.FromMinutes(16));
            await FireAsync();
            var run = (await RunsOfAsync(id)).Last();
            await SetRunStatusAsync(run.Id, outcome);
            await FireAsync(); // settles the finished run
            return run;
        }

        await TickAsync(RunStatuses.Failed);
        await TickAsync(RunStatuses.TimedOut);
        Assert.Equal((ScheduleStatuses.Active, 2), ((await RowAsync(id)).Status, (await RowAsync(id)).FailuresInARow));
        await TickAsync(RunStatuses.Succeeded);
        Assert.Equal(0, (await RowAsync(id)).FailuresInARow);
        await TickAsync(RunStatuses.Failed);
        await TickAsync(RunStatuses.Expired);
        await TickAsync(RunStatuses.Failed);

        var row = await RowAsync(id);
        Assert.Equal((ScheduleStatuses.Paused, ScheduleReasons.Failures, 3), (row.Status, row.PauseReason, row.FailuresInARow));
        var before = (await RunsOfAsync(id)).Count;
        api.Clock.Advance(TimeSpan.FromMinutes(16));
        await FireAsync();
        Assert.Equal(before, (await RunsOfAsync(id)).Count); // paused: nothing fires
    }

    [Fact]
    public async Task A_cancelled_run_is_neither_a_failure_nor_a_success()
    {
        var team = await TeamAsync(api);
        var job = await ActiveJobAsync(team);
        var id = (await CreateAsync(team, job.Id, 15)).GetProperty("id").GetGuid();
        api.Clock.Advance(TimeSpan.FromMinutes(16));
        await FireAsync();
        await SetRunStatusAsync((await RunsOfAsync(id)).Single().Id, RunStatuses.Cancelled);
        await FireAsync();
        var row = await RowAsync(id);
        Assert.Equal((ScheduleStatuses.Active, 0, true), (row.Status, row.FailuresInARow, row.LastRunSettled));
    }

    [Fact]
    public async Task With_the_switch_off_a_tick_is_skipped_and_the_schedule_stays_as_it_is()
    {
        var team = await TeamAsync(api);
        var job = await ActiveJobAsync(team);
        var id = (await CreateAsync(team, job.Id, 15)).GetProperty("id").GetGuid();
        await SetSchedulesAsync(team, false);
        api.Clock.Advance(TimeSpan.FromMinutes(16));
        await FireAsync();
        Assert.Empty(await RunsOfAsync(id));
        Assert.Equal(ScheduleStatuses.Active, (await RowAsync(id)).Status);

        await SetSchedulesAsync(team, true);
        api.Clock.Advance(TimeSpan.FromMinutes(16));
        await FireAsync();
        Assert.Single(await RunsOfAsync(id));
    }

    [Fact]
    public async Task A_machine_that_became_production_a_level_that_dropped_and_a_retired_job_each_stop_the_schedule_with_their_reason()
    {
        var strict = await TeamAsync(api, execLevel: ExecLevels.Shell);
        await ScriptKit.ProfileAsync(strict, ExecLevels.Shell, [ScriptKit.Sh]);
        await ScriptKit.SetRoleAsync(strict, "test", "application");
        var strictJob = await ActiveJobAsync(strict);
        var strictId = (await CreateAsync(strict, strictJob.Id, 15)).GetProperty("id").GetGuid();
        await ScriptKit.SetRoleAsync(strict, "production", "application");
        api.Clock.Advance(TimeSpan.FromMinutes(16));
        await FireAsync();
        Assert.Equal((ScheduleStatuses.Paused, ScheduleReasons.Strict), ((await RowAsync(strictId)).Status, (await RowAsync(strictId)).PauseReason));
        Assert.Empty(await RunsOfAsync(strictId));

        var level = await TeamAsync(api);
        var levelId = (await CreateAsync(level, (await ActiveJobAsync(level)).Id, 15)).GetProperty("id").GetGuid();
        await RemoteKit.ProfileAsync(level.Target, ExecLevels.Off);
        api.Clock.Advance(TimeSpan.FromMinutes(16));
        await FireAsync();
        Assert.Equal((ScheduleStatuses.Paused, ScheduleReasons.ExecLevel), ((await RowAsync(levelId)).Status, (await RowAsync(levelId)).PauseReason));

        var retired = await TeamAsync(api);
        var retiredJob = await ActiveJobAsync(retired);
        var retiredId = (await CreateAsync(retired, retiredJob.Id, 15)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await retired.Owner.PostAsync($"/api/jobs/{retiredJob.Id}/retire")).StatusCode);
        Assert.Equal((ScheduleStatuses.Ended, ScheduleReasons.JobRetired), ((await RowAsync(retiredId)).Status, (await RowAsync(retiredId)).PauseReason));
    }

    [Fact]
    public async Task The_remote_housekeeping_fires_due_schedules_too()
    {
        var team = await TeamAsync(api);
        var job = await ActiveJobAsync(team);
        var id = (await CreateAsync(team, job.Id, 15)).GetProperty("id").GetGuid();
        api.Clock.Advance(TimeSpan.FromMinutes(16));
        await RemoteKit.HousekeepAsync(api);
        Assert.Single(await RunsOfAsync(id));
    }

    // ---- controlling ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_owner_or_an_admin_pauses_at_once_only_the_owner_starts_it_again_and_it_starts_over()
    {
        var team = await TeamAsync(api);
        var job = await ActiveJobAsync(team);
        var id = (await CreateAsync(team, job.Id, 15)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await team.Other.PostAsync($"/api/schedules/{id}/pause")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Admin.PostAsync($"/api/schedules/{id}/pause")).StatusCode);
        var row = await RowAsync(id);
        Assert.Equal((ScheduleStatuses.Paused, ScheduleReasons.Owner), (row.Status, row.PauseReason));
        await RemoteKit.AssertProblemAsync(await team.Admin.PostAsync($"/api/schedules/{id}/pause"), HttpStatusCode.Conflict, RemoteErrors.NotPending);

        Assert.Equal(HttpStatusCode.NotFound, (await team.Admin.PostAsync($"/api/schedules/{id}/resume", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/schedules/{id}/resume", new { })).StatusCode);
        row = await RowAsync(id);
        Assert.Equal((ScheduleStatuses.Active, 0), (row.Status, row.FailuresInARow));
        Assert.Null(row.PauseReason);
        Assert.True(row.NextRunAt > api.Clock.GetUtcNow());
    }

    [Fact]
    public async Task A_schedule_cannot_start_again_while_schedules_are_off_or_after_it_ended_and_the_owner_or_an_admin_ends_it_for_good()
    {
        var team = await TeamAsync(api);
        var job = await ActiveJobAsync(team);
        var id = (await CreateAsync(team, job.Id, 15)).GetProperty("id").GetGuid();
        await team.Owner.PostAsync($"/api/schedules/{id}/pause");
        await SetSchedulesAsync(team, false);
        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync($"/api/schedules/{id}/resume", new { }), HttpStatusCode.Conflict, RemoteErrors.SchedulesDisabled);
        await SetSchedulesAsync(team, true);

        Assert.Equal(HttpStatusCode.NoContent, (await team.Admin.PostAsync($"/api/schedules/{id}/end")).StatusCode);
        Assert.Equal(ScheduleStatuses.Ended, (await RowAsync(id)).Status);
        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync($"/api/schedules/{id}/resume", new { }), HttpStatusCode.Conflict, RemoteErrors.NotPending);
        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync($"/api/schedules/{id}/end"), HttpStatusCode.Conflict, RemoteErrors.NotPending);
    }

    [Fact]
    public async Task The_workspace_setting_is_read_and_written_and_defaults_to_off()
    {
        var team = await TeamAsync(api, schedules: false);
        var before = await team.Admin.GetJsonAsync($"/api/workspaces/{team.Admin.WorkspaceId}/remote-settings");
        Assert.False(before.GetProperty("remoteSchedulesEnabled").GetBoolean());
        await SetSchedulesAsync(team, true);
        Assert.True((await team.Admin.GetJsonAsync($"/api/workspaces/{team.Admin.WorkspaceId}/remote-settings")).GetProperty("remoteSchedulesEnabled").GetBoolean());
        var denied = await team.Owner.SendAsync(HttpMethod.Put, $"/api/workspaces/{team.Admin.WorkspaceId}/remote-settings", new { remoteSchedulesEnabled = false });
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode); // a member who is not an admin cannot change it
    }
}
