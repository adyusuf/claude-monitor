using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>
/// A workspace is never left without an owner: two owners removing (or demoting) each other at the same moment both saw the
/// other as an owner, so the owners and the target are locked before the last-owner check and it reads them again.
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

    /// <summary>Exactly one of the two succeeded; the other got the last-owner refusal, and one owner is left.</summary>
    private async Task AssertOneWonAsync(HttpResponseMessage[] answers, string field, Guid workspaceId)
    {
        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.NoContent);
        var refused = Assert.Single(answers, a => a.StatusCode == HttpStatusCode.BadRequest);
        var errors = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("last_owner", errors.GetProperty(field)[0].GetString());
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

        await AssertOneWonAsync(answers, "userId", first.WorkspaceId);
    }

    [Fact]
    public async Task Two_owners_demoting_each_other_with_both_checks_passed_leave_one_owner()
    {
        var (first, second) = await TwoOwnersAsync();

        var answers = await HeldTogetherAsync(first, second, DemoteAsync);

        await AssertOneWonAsync(answers, "role", first.WorkspaceId);
    }

    [Fact]
    public async Task Two_owners_removing_each_other_at_the_same_moment_leave_one_owner_every_time()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var (first, second) = await TwoOwnersAsync();

            var answers = await TogetherAsync(new Func<Task<HttpResponseMessage>>[] { () => RemoveAsync(first.WorkspaceId, first, second), () => RemoveAsync(first.WorkspaceId, second, first) });

            await AssertOneWonAsync(answers, "userId", first.WorkspaceId);
        }
    }

    [Fact]
    public async Task Two_owners_demoting_each_other_at_the_same_moment_leave_one_owner_every_time()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var (first, second) = await TwoOwnersAsync();

            var answers = await TogetherAsync(new Func<Task<HttpResponseMessage>>[] { () => DemoteAsync(first.WorkspaceId, first, second), () => DemoteAsync(first.WorkspaceId, second, first) });

            await AssertOneWonAsync(answers, "role", first.WorkspaceId);
        }
    }
}
