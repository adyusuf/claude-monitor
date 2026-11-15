using Podium.Api.Data;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Podium.Api.Tests;

/// <summary>An import that fails for a reason that is not the tracker's (here a database that refuses a write) closes its run as failed, as a pass does, and throws the error on.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationImportFailureTests(ApiFactory api)
{
    private async Task<List<IntegrationSyncRun>> RunsAsync(JiraWorld w)
    {
        await using var db = api.Db();
        return await db.IntegrationSyncRuns.AsNoTracking().Where(r => r.ConnectionId == w.ConnectionId && r.Kind == SyncKinds.Import).OrderBy(r => r.Id).ToListAsync();
    }

    [Fact]
    public async Task A_space_import_that_fails_unexpectedly_closes_its_run_as_failed_and_throws_the_error_on()
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space("IFA").Issue("One"));
        var fault = new CommandFault("INSERT INTO issues") { Error = new InvalidOperationException("the database refused the write") };
        await using var host = HostKit.WithInterceptor(api, fault);
        await using var scope = host.Services.CreateAsyncScope();
        var connection = await JiraKit.ConnectionAsync(w);

        await Assert.ThrowsAnyAsync<Exception>(() => scope.ServiceProvider.GetRequiredService<ImportService>().ImportSpacesAsync(connection, ["IFA"], w.Admin.Id, CancellationToken.None));

        var run = Assert.Single(await RunsAsync(w));
        Assert.Equal((SyncStatuses.Failed, SyncService.InternalError, true), (run.Status, run.Error, run.EndedAt is not null));
    }

    [Fact]
    public async Task A_sprint_import_that_fails_unexpectedly_closes_its_run_as_failed_and_throws_the_error_on()
    {
        var s = await JiraSprintKit.ImportedAsync(api);
        var fault = new CommandFault("INSERT INTO sprints") { Error = new InvalidOperationException("the database refused the write") };
        await using var host = HostKit.WithInterceptor(api, fault);
        await using var scope = host.Services.CreateAsyncScope();
        var connection = await JiraKit.ConnectionAsync(s.World);

        await Assert.ThrowsAnyAsync<Exception>(() => scope.ServiceProvider.GetRequiredService<ImportService>()
            .ImportSprintsAsync(connection, "SPR", [s.Active.IdText], CancellationToken.None));

        var runs = await RunsAsync(s.World);
        var run = runs[^1]; // the first run is the import of the space itself
        Assert.Equal((SyncStatuses.Failed, SyncService.InternalError, true), (run.Status, run.Error, run.EndedAt is not null));
        Assert.DoesNotContain(runs, r => r.Status == SyncStatuses.Running);
    }
}
