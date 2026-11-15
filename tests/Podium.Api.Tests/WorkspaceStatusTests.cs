using Microsoft.EntityFrameworkCore;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>A workspace's status is one of the named constants; an archived one is gone from the person's own list.</summary>
[Collection(ApiGroup.Name)]
public sealed class WorkspaceStatusTests(ApiFactory api)
{
    [Fact]
    public void The_status_values_are_the_ones_the_database_constraint_and_the_clients_know()
    {
        Assert.Equal("active", WorkspaceStatuses.Active);
        Assert.Equal("archived", WorkspaceStatuses.Archived);
    }

    [Fact]
    public async Task An_archived_workspace_is_not_in_the_list_of_the_person_who_belonged_to_it()
    {
        var user = await api.NewClient().SignedUpAsync("archived-ws");
        var before = await user.GetJsonAsync("/api/me");
        Assert.Contains(before.GetProperty("workspaces").EnumerateArray(), w => w.GetProperty("id").GetGuid() == user.WorkspaceId);

        await using (var db = api.Db())
        {
            await db.Workspaces.Where(w => w.Id == user.WorkspaceId)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, WorkspaceStatuses.Archived));
        }

        var after = await user.GetJsonAsync("/api/me");
        Assert.DoesNotContain(after.GetProperty("workspaces").EnumerateArray(), w => w.GetProperty("id").GetGuid() == user.WorkspaceId);
    }
}
