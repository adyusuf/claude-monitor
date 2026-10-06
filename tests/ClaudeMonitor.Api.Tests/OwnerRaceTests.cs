using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>
/// A workspace is never left without an owner: two owners removing (or demoting) each other at the same moment both saw the
/// other as an owner, so the owners, the target and the actor are locked before the checks and read again: a request whose
/// actor was demoted or removed while it waited is refused, not completed with the role it began with.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class OwnerRaceTests(ApiFactory api)
{
    private const int Rounds = 12;

    /// <summary>One workspace with two owners; the first owns it from sign-up, the second is invited as owner.</summary>
    private async Task<(TestUser First, TestUser Second)> TwoOwnersAsync()
    {
        var first = await api.NewClient().SignedUpAsync("or-first");
        var second = await RemoteKit.MemberAsync(api, first, "or-second", Roles.Owner);
        return (first, second);
    }

    private delegate Task<HttpResponseMessage> Act(Guid workspaceId, TestUser by, TestUser target);

    private static Task<HttpResponseMessage> RemoveAsync(Guid workspaceId, TestUser by, TestUser target) =>
        by.SendAsync(HttpMethod.Delete, $"/api/workspaces/{workspaceId}/members/{target.Id}");

    private static Task<HttpResponseMessage> DemoteAsync(Guid workspaceId, TestUser by, TestUser target) =>
        by.SendAsync(HttpMethod.Patch, $"/api/workspaces/{workspaceId}/members/{target.Id}", new { role = Roles.Admin });

    private static async Task<T[]> TogetherAsync<T>(IEnumerable<Func<Task<T>>> actions)
    {
        var start = new TaskCompletionSource();
        var running = actions.Select(a => Task.Run(async () =>
        {
            await start.Task;
            return await a();
        })).ToArray();
        start.SetResult();
        return await Task.WhenAll(running);
    }

    private async Task<int> OwnersAsync(Guid workspaceId)
    {
        await using var db = api.Db();
        return await db.WorkspaceMembers.CountAsync(m => m.WorkspaceId == workspaceId && m.RemovedAt == null && m.Role == Roles.Owner);
    }

    /// <summary>
    /// Exactly one of the two succeeded and one owner is left. The other is refused for what its actor now is: a removed
    /// owner is no member (404), a demoted one is an admin who cannot touch an owner (403).
    /// </summary>
    private async Task AssertOneWonAsync(HttpResponseMessage[] answers, HttpStatusCode refusal, Guid workspaceId)
    {
        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(answers, a => a.StatusCode == refusal);
        Assert.Equal(1, await OwnersAsync(workspaceId));
    }

    private async Task<HttpResponseMessage[]> HeldTogetherAsync(TestUser first, TestUser second, Act act)
    {
        await using var holder = api.Db();
        await using var hold = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM workspace_members WHERE workspace_id = {first.WorkspaceId} AND user_id IN ({first.Id}, {second.Id}) FOR UPDATE");
        var one = Task.Run(() => act(first.WorkspaceId, first, second));
        var other = Task.Run(() => act(first.WorkspaceId, second, first));
        await WaitUntilAsync(async () => await WaitingBackendsAsync() >= 2);
        await hold.CommitAsync();
        return await Task.WhenAll(one, other);
    }

    private async Task<int> WaitingBackendsAsync()
    {
        await using var db = api.Db();
        return await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE wait_event_type = 'Lock'").SingleAsync();
    }

    /// <summary>Waits on a condition, never a fixed time; fails after ten seconds.</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition()) await Task.Delay(20, timeout.Token);
    }

    /// <summary>Both removals have passed the check (their target row is held) when the hold ends; only one may go through.</summary>
    [Fact]
    public async Task Two_owners_removing_each_other_with_both_checks_passed_leave_one_owner()
    {
        var (first, second) = await TwoOwnersAsync();

        var answers = await HeldTogetherAsync(first, second, RemoveAsync);

        await AssertOneWonAsync(answers, HttpStatusCode.NotFound, first.WorkspaceId);
    }

    [Fact]
    public async Task Two_owners_demoting_each_other_with_both_checks_passed_leave_one_owner()
    {
        var (first, second) = await TwoOwnersAsync();

        var answers = await HeldTogetherAsync(first, second, DemoteAsync);

        await AssertOneWonAsync(answers, HttpStatusCode.Forbidden, first.WorkspaceId);
    }

    [Fact]
    public async Task Two_owners_removing_each_other_at_the_same_moment_leave_one_owner_every_time()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var (first, second) = await TwoOwnersAsync();

            var answers = await TogetherAsync(new Func<Task<HttpResponseMessage>>[] { () => RemoveAsync(first.WorkspaceId, first, second), () => RemoveAsync(first.WorkspaceId, second, first) });

            await AssertOneWonAsync(answers, HttpStatusCode.NotFound, first.WorkspaceId);
        }
    }

    [Fact]
    public async Task Two_owners_demoting_each_other_at_the_same_moment_leave_one_owner_every_time()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var (first, second) = await TwoOwnersAsync();

            var answers = await TogetherAsync(new Func<Task<HttpResponseMessage>>[] { () => DemoteAsync(first.WorkspaceId, first, second), () => DemoteAsync(first.WorkspaceId, second, first) });

            await AssertOneWonAsync(answers, HttpStatusCode.Forbidden, first.WorkspaceId);
        }
    }

    /// <summary>Every request has read its actor (the rows in <paramref name="held"/> are locked meanwhile) before any of them locks.</summary>
    private async Task<HttpResponseMessage[]> HeldActorsAsync(Guid workspaceId, Guid[] held, params Func<Task<HttpResponseMessage>>[] actions)
    {
        await using var holder = api.Db();
        await using var hold = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM workspace_members WHERE workspace_id = {workspaceId} AND user_id = ANY({held}) FOR UPDATE");
        var running = actions.Select(a => Task.Run(a)).ToArray();
        await WaitUntilAsync(async () => await WaitingBackendsAsync() >= actions.Length);
        await hold.CommitAsync();
        return await Task.WhenAll(running);
    }

    private async Task<WorkspaceMember> RowAsync(Guid workspaceId, TestUser user)
    {
        await using var db = api.Db();
        return await db.WorkspaceMembers.AsNoTracking().SingleAsync(m => m.WorkspaceId == workspaceId && m.UserId == user.Id);
    }

    /// <summary>
    /// Three owners; A demotes B while B removes A, both having read themselves as owners. Either runs first and the other is
    /// refused (404): B demoted can no longer remove anyone, A removed can no longer demote anyone. Never both applied.
    /// </summary>
    [Fact]
    public async Task An_owner_demoted_while_removing_another_owner_does_not_remove_them()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var a = await api.NewClient().SignedUpAsync("or-a");
            var b = await RemoteKit.MemberAsync(api, a, "or-b", Roles.Owner);
            await RemoteKit.MemberAsync(api, a, "or-c", Roles.Owner);
            var workspaceId = a.WorkspaceId;

            var answers = await HeldActorsAsync(workspaceId, [a.Id, b.Id],
                () => ChangeAsync(workspaceId, a, b, Roles.Viewer), () => RemoveAsync(workspaceId, b, a));

            var (aRow, bRow) = (await RowAsync(workspaceId, a), await RowAsync(workspaceId, b));
            if (bRow.Role == Roles.Viewer)
            {
                Assert.Null(aRow.RemovedAt);
                Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NotFound), (answers[0].StatusCode, answers[1].StatusCode));
            }
            else
            {
                Assert.Equal(Roles.Owner, bRow.Role);
                Assert.NotNull(aRow.RemovedAt);
                Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NoContent), (answers[0].StatusCode, answers[1].StatusCode));
            }
        }

    }

    /// <summary>An owner just demoted to viewer cannot grant ownership with the role it had when its request began.</summary>
    [Fact]
    public async Task An_owner_demoted_while_granting_ownership_does_not_grant_it()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var a = await api.NewClient().SignedUpAsync("og-a");
            var b = await RemoteKit.MemberAsync(api, a, "og-b", Roles.Owner);
            var target = await RemoteKit.MemberAsync(api, a, "og-t", Roles.Admin);
            var workspaceId = a.WorkspaceId;

            var answers = await HeldActorsAsync(workspaceId, [a.Id, b.Id],
                () => ChangeAsync(workspaceId, a, b, Roles.Viewer), () => ChangeAsync(workspaceId, b, target, Roles.Owner));

            Assert.Equal(HttpStatusCode.NoContent, answers[0].StatusCode);
            var targetRow = await RowAsync(workspaceId, target);
            await using var db = api.Db();
            var changes = await db.AuditEvents.AsNoTracking().Where(e => e.WorkspaceId == workspaceId && e.Action == AuditActions.RoleChanged)
                .OrderBy(e => e.Id).Select(e => e.ActorUserId).ToListAsync();
            if (answers[1].StatusCode == HttpStatusCode.NoContent)
            {
                // B granted it first, as an owner: it is audited before B's own demotion.
                Assert.Equal(Roles.Owner, targetRow.Role);
                Assert.Equal(new Guid?[] { b.Id, a.Id }, changes);
            }
            else
            {
                // A demoted B first: B, now a viewer, may not change anyone's role.
                Assert.Equal(HttpStatusCode.NotFound, answers[1].StatusCode);
                Assert.Equal(Roles.Admin, targetRow.Role);
                Assert.Equal(new Guid?[] { a.Id }, changes);
            }
        }
    }

    /// <summary>An admin removed while it removes a member: the removal it began as an admin does not complete after its own.</summary>
    [Fact]
    public async Task An_admin_removed_while_removing_a_member_does_not_complete_the_removal_afterwards()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var owner = await api.NewClient().SignedUpAsync("ar-o");
            var admin = await RemoteKit.MemberAsync(api, owner, "ar-a", Roles.Admin);
            var member = await RemoteKit.MemberAsync(api, owner, "ar-m");
            var workspaceId = owner.WorkspaceId;

            var answers = await HeldActorsAsync(workspaceId, [owner.Id, admin.Id],
                () => RemoveAsync(workspaceId, owner, admin), () => RemoveAsync(workspaceId, admin, member));

            Assert.Equal(HttpStatusCode.NoContent, answers[0].StatusCode);
            await using var db = api.Db();
            var removals = await db.AuditEvents.AsNoTracking().Where(e => e.WorkspaceId == workspaceId && e.Action == AuditActions.MemberRemoved)
                .OrderBy(e => e.Id).Select(e => e.ActorUserId).ToListAsync();
            if (answers[1].StatusCode == HttpStatusCode.NoContent)
            {
                // The admin's removal came first: it is audited before the admin's own.
                Assert.Equal(new Guid?[] { admin.Id, owner.Id }, removals);
            }
            else
            {
                Assert.Equal(HttpStatusCode.NotFound, answers[1].StatusCode);
                Assert.Equal(new Guid?[] { owner.Id }, removals);
                Assert.Null((await RowAsync(workspaceId, member)).RemovedAt);
            }
        }
    }

    private static Task<HttpResponseMessage> ChangeAsync(Guid workspaceId, TestUser by, TestUser target, string role) =>
        by.SendAsync(HttpMethod.Patch, $"/api/workspaces/{workspaceId}/members/{target.Id}", new { role });
}
