using Podium.Api.Background;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>A chore that keeps failing does not stop retention, which runs last; the failure is still thrown for the loop to log.</summary>
[Collection(ApiGroup.Name)]
public sealed class HousekeepingIsolationTests(ApiFactory api)
{
    /// <summary>A finished run of a workspace with one day of retention, two days old.</summary>
    private async Task<Guid> OldFinishedRunAsync()
    {
        var team = await RemoteKit.TeamAsync(api);
        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/workspaces/{team.WorkspaceId}/settings", new { retentionDays = 1 })).EnsureSuccessStatusCode();
        var run = (await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()))).Id;
        (await team.Admin.PostAsync($"/api/runs/{run}/cancel")).EnsureSuccessStatusCode();
        api.Clock.Advance(TimeSpan.FromDays(2));
        return run;
    }

    private async Task<bool> RunStillThereAsync(Guid run)
    {
        await using var db = api.Db();
        return await db.RemoteRuns.AnyAsync(r => r.Id == run);
    }

    [Fact]
    public async Task A_failing_remote_chore_does_not_stop_retention()
    {
        var run = await OldFinishedRunAsync();
        var fault = new CommandFault("UPDATE machine_grants") { Error = new DatabaseDownException() };
        await using var host = HostKit.WithInterceptor(api, fault);

        await using var scope = host.Services.CreateAsyncScope();
        var error = await Record.ExceptionAsync(() => Housekeeper.PassAsync(scope.ServiceProvider, api.Clock, CancellationToken.None));

        Assert.False(await RunStillThereAsync(run));
        Assert.IsType<DatabaseDownException>(Assert.Single(Assert.IsType<AggregateException>(error).Flatten().InnerExceptions));
    }

    [Fact]
    public async Task A_failing_partition_chore_does_not_stop_the_remote_chores()
    {
        var run = await OldFinishedRunAsync();
        var fault = new CommandFault("FROM pg_class") { Error = new DatabaseDownException() };
        await using var host = HostKit.WithInterceptor(api, fault);

        await using var scope = host.Services.CreateAsyncScope();
        var error = await Record.ExceptionAsync(() => Housekeeper.PassAsync(scope.ServiceProvider, api.Clock, CancellationToken.None));

        Assert.False(await RunStillThereAsync(run));
        Assert.IsType<DatabaseDownException>(Assert.Single(Assert.IsType<AggregateException>(error).Flatten().InnerExceptions));
    }
}
