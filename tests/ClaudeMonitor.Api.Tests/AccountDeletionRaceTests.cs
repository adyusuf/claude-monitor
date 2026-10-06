using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>
/// Deleting an account never leaves a workspace that still exists without an owner: the sole-owner check runs under the
/// locks of the workspace's owners, so two owners deleting at the same moment cannot each see the other as the one who stays.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class AccountDeletionRaceTests(ApiFactory api)
{
    private const int Rounds = 8;

    private static Task<HttpResponseMessage> DeleteAsync(TestUser user) =>
        user.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" });

    private async Task<(int Owners, int Members, string Status)> StateAsync(Guid workspaceId)
    {
        await using var db = api.Db();
        var rows = await db.WorkspaceMembers.AsNoTracking().Where(m => m.WorkspaceId == workspaceId && m.RemovedAt == null).ToListAsync();
        var status = await db.Workspaces.AsNoTracking().Where(w => w.Id == workspaceId).Select(w => w.Status).SingleAsync();
        return (rows.Count(m => m.Role == Roles.Owner), rows.Count, status);
    }

    /// <summary>Both owners passed their checks before either deleted; one is refused, the workspace and its member stay.</summary>
    [Fact]
    public async Task Two_owners_deleting_at_the_same_moment_leave_the_shared_workspace_an_owner()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var first = await api.NewClient().SignedUpAsync("del-first");
            var second = await RemoteKit.MemberAsync(api, first, "del-second", Roles.Owner);
            await RemoteKit.MemberAsync(api, first, "del-member");
            var workspaceId = first.WorkspaceId;

            var answers = await MemberLockHold.RunAsync(api, workspaceId, [first.Id, second.Id], () => DeleteAsync(first), () => DeleteAsync(second));

            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.NoContent);
            var refused = Assert.Single(answers, a => a.StatusCode == HttpStatusCode.Conflict);
            var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("sole_owner", body.GetProperty("title").GetString());
            Assert.Equal(workspaceId, body.GetProperty("workspaces")[0].GetGuid());
            var state = await StateAsync(workspaceId);
            Assert.Equal((1, 2, WorkspaceStatuses.Active), state);
        }
    }

    /// <summary>With nobody else in it, the last to leave archives the workspace instead of abandoning it active and ownerless.</summary>
    [Fact]
    public async Task Two_owners_of_a_two_person_workspace_deleting_together_do_not_abandon_it_active()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var first = await api.NewClient().SignedUpAsync("del-pair-a");
            var second = await RemoteKit.MemberAsync(api, first, "del-pair-b", Roles.Owner);
            var workspaceId = first.WorkspaceId;

            var answers = await MemberLockHold.RunAsync(api, workspaceId, [first.Id, second.Id], () => DeleteAsync(first), () => DeleteAsync(second));

            Assert.All(answers, a => Assert.Equal(HttpStatusCode.NoContent, a.StatusCode));
            var (owners, members, status) = await StateAsync(workspaceId);
            Assert.Equal((0, 0), (owners, members));
            Assert.Equal("archived", status);
        }
    }
}
