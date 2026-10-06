using System.Net;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>
/// Accounts that belong to several workspaces are deleted while other transactions lock one of those workspaces' members:
/// every transaction takes member rows in one order (workspace id, then user id), so none of them waits for a row another
/// holds while holding one it needs (PostgreSQL 40P01 would surface as a 500), and no workspace that still exists is left
/// without an owner.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class AccountDeletionOrderTests(ApiFactory api)
{
    private const int Rounds = 8;

    private static Task<HttpResponseMessage> DeleteAsync(TestUser user) =>
        user.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" });

    private static Task<HttpResponseMessage> RemoveAsync(Guid workspaceId, TestUser by, TestUser target) =>
        by.SendAsync(HttpMethod.Delete, $"/api/workspaces/{workspaceId}/members/{target.Id}");

    private static async Task JoinAsync(ApiFactory api, TestUser owner, TestUser user)
    {
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = user.Email, role = Roles.Owner })).EnsureSuccessStatusCode();
        (await user.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(user.Email) })).EnsureSuccessStatusCode();
    }

    private static async Task AssertAnsweredAsync(string what, HttpResponseMessage answer, params HttpStatusCode[] allowed) =>
        Assert.True(allowed.Contains(answer.StatusCode), $"{what}: {(int)answer.StatusCode} {await answer.Content.ReadAsStringAsync()}");

    /// <summary>
    /// Two users who are owners of the same three workspaces, joined in opposite orders, delete their accounts at the same
    /// moment, while one workspace's creator removes the first from it and another's deletes their own account. All five
    /// requests wait on member rows before any runs: the outcome is the answers and the owners that remain, never a deadlock.
    /// </summary>
    [Fact]
    public async Task Accounts_in_several_workspaces_deleted_together_with_other_member_changes_never_deadlock_or_leave_a_workspace_ownerless()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var first = await api.NewClient().SignedUpAsync("ord-first");
            var second = await api.NewClient().SignedUpAsync("ord-second");
            var (a, b, c) = (await api.NewClient().SignedUpAsync("ord-a"), await api.NewClient().SignedUpAsync("ord-b"), await api.NewClient().SignedUpAsync("ord-c"));
            await RemoteKit.MemberAsync(api, b, "ord-plain");
            foreach (var owner in new[] { a, b, c }) await JoinAsync(api, owner, first);
            foreach (var owner in new[] { c, b, a }) await JoinAsync(api, owner, second);
            Guid[] shared = [a.WorkspaceId, b.WorkspaceId, c.WorkspaceId];

            var answers = await MemberLockHold.RunHoldingWorkspacesAsync(api, shared,
                () => DeleteAsync(first), () => DeleteAsync(second), () => RemoveAsync(a.WorkspaceId, a, first),
                () => DeleteAsync(b), () => RemoveAsync(c.WorkspaceId, c, second));

            await AssertAnsweredAsync("first deleted", answers[0], HttpStatusCode.NoContent, HttpStatusCode.NotFound, HttpStatusCode.Conflict);
            await AssertAnsweredAsync("second deleted", answers[1], HttpStatusCode.NoContent, HttpStatusCode.NotFound, HttpStatusCode.Conflict);
            await AssertAnsweredAsync("first removed", answers[2], HttpStatusCode.NoContent, HttpStatusCode.NotFound);
            await AssertAnsweredAsync("b deleted", answers[3], HttpStatusCode.NoContent, HttpStatusCode.NotFound, HttpStatusCode.Conflict);
            await AssertAnsweredAsync("second removed", answers[4], HttpStatusCode.NoContent, HttpStatusCode.NotFound);

            await using var db = api.Db();
            var everyWorkspace = shared.Concat([first.WorkspaceId, second.WorkspaceId]).ToArray();
            foreach (var workspace in await db.Workspaces.AsNoTracking().Where(w => everyWorkspace.Contains(w.Id) && w.Status != "archived").ToListAsync())
            {
                var owners = await db.WorkspaceMembers.CountAsync(m => m.WorkspaceId == workspace.Id && m.RemovedAt == null && m.Role == Roles.Owner);
                Assert.True(owners >= 1, $"workspace {workspace.Id} is active and has no owner");
            }

            // An answer of 204 is a deleted account; a refusal wrote nothing.
            foreach (var (user, answer) in new[] { (first, answers[0]), (second, answers[1]), (b, answers[3]) })
            {
                var status = await db.Users.AsNoTracking().Where(u => u.Id == user.Id).Select(u => u.Status).SingleAsync();
                Assert.Equal(answer.StatusCode == HttpStatusCode.NoContent ? UserStatuses.Deleted : UserStatuses.Active, status);
            }
        }
    }
}
