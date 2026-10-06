using System.Net;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>
/// The cascades of remote work (a member removed, an agent revoked) take their row locks in one global order (RemoteLocks),
/// so two of them over overlapping rows never wait on each other (40P01). Each test holds a row in a separate transaction to
/// stop a cascade at a known place, then looks at what that cascade holds, or lets a second one in.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class CascadeLockOrderTests(ApiFactory api)
{
    private DateTimeOffset Now => api.Clock.GetUtcNow();

    /// <summary>A grant whose id sorts by <paramref name="age"/> (older first), so the order of ids is the test's to choose.</summary>
    private MachineGrant Grant(RemoteTeam team, Guid target, Guid owner, Guid? granteeAgent, int age) => new()
    {
        Id = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddMinutes(-age)),
        WorkspaceId = team.WorkspaceId,
        TargetAgentId = target,
        OwnerUserId = owner,
        GranteeUserId = team.Admin.Id,
        GranteeAgentId = granteeAgent,
        MaxTimeoutSeconds = 60,
        TemplateHash = Guid.NewGuid().ToString("N"),
        Status = GrantStatuses.Active,
        CreatedAt = Now,
        ExpiresAt = Now.AddDays(30),
    };

    private async Task InsertAsync(params object[] rows)
    {
        await using var db = api.Db();
        foreach (var row in rows)
        {
            db.Add(row);
            await db.SaveChangesAsync();
        }
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

    /// <summary>
    /// Removing a member with two agents while another agent is revoked, over rows both cascades reach. Held at a grant of the
    /// member's second agent, the removal has (without a global order) already locked a run of its first agent; the revoke
    /// then locks the grant that links its agent to the second one and waits for that run, and when the hold ends each needs
    /// what the other holds. With the order the removal has locked no run yet, so the revoke goes through and the removal follows.
    /// </summary>
    [Fact]
    public async Task A_member_removal_and_an_agent_revoke_over_the_same_grants_and_runs_do_not_deadlock()
    {
        var team = await RemoteKit.TeamAsync(api);
        var first = team.Target;
        var second = await team.Owner.ConnectAgentAsync(team.WorkspaceId);
        var other = await RemoteKit.MemberAsync(api, team.Admin, "cl-other");
        var revoked = await other.ConnectAgentAsync(team.WorkspaceId);
        var run = new RemoteRun
        {
            WorkspaceId = team.WorkspaceId,
            RequesterAgentId = first.Tokens.AgentId,
            RequesterUserId = team.Owner.Id,
            TargetAgentId = revoked.Tokens.AgentId,
            TargetUserId = other.Id,
            ClientKey = RemoteKit.NewKey(),
            Argv = [RemoteKit.Tail],
            TimeoutSeconds = 30,
            CreatedAt = Now,
            ExpiresAt = Now.AddHours(1),
        };
        var stopsRemoval = Grant(team, second.Tokens.AgentId, team.Owner.Id, null, 2);
        var linksBoth = Grant(team, revoked.Tokens.AgentId, other.Id, second.Tokens.AgentId, 1);
        await InsertAsync(run, stopsRemoval, linksBoth);
        await using var holder = api.Db();
        await using var hold = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlAsync($"SELECT 1 FROM machine_grants WHERE id = {stopsRemoval.Id} FOR UPDATE");

        var removal = Task.Run(() => team.Admin.SendAsync(HttpMethod.Delete, $"/api/workspaces/{team.WorkspaceId}/members/{team.Owner.Id}"));
        await WaitUntilAsync(async () => removal.IsCompleted || await WaitingBackendsAsync() >= 1);
        var revoke = Task.Run(() => team.Admin.PostAsync($"/api/agents/{revoked.Tokens.AgentId}/revoke"));
        await WaitUntilAsync(async () => revoke.IsCompleted || await WaitingBackendsAsync() >= 2);
        await hold.CommitAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await removal).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await revoke).StatusCode);
        Assert.Equal(RunStatuses.Cancelled, (await RemoteKit.RunRowAsync(api, run.Id)).Status);
        await using var db = api.Db();
        Assert.All(await db.MachineGrants.AsNoTracking().Where(g => g.Id == stopsRemoval.Id || g.Id == linksBoth.Id).ToListAsync(),
            g => Assert.Equal(GrantStatuses.Revoked, g.Status));
    }

    /// <summary>
    /// One cascade over three grants stopped at the middle one: it has locked the first and not the third, whatever order the
    /// rows lie in on disk (they are written third, second, first, so an unordered update reaches the third first).
    /// </summary>
    [Fact]
    public async Task A_cascade_locks_the_grants_it_changes_in_id_order()
    {
        var team = await RemoteKit.TeamAsync(api);
        var target = team.TargetId;
        var (first, middle, last) = (Grant(team, target, team.Owner.Id, null, 3), Grant(team, target, team.Owner.Id, null, 2),
            Grant(team, target, team.Owner.Id, null, 1));
        await InsertAsync(last, middle, first);
        await using var holder = api.Db();
        await using var hold = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlAsync($"SELECT 1 FROM machine_grants WHERE id = {middle.Id} FOR UPDATE");

        var revoke = Task.Run(() => team.Owner.PostAsync($"/api/agents/{target}/revoke"));
        await WaitUntilAsync(async () => revoke.IsCompleted || await WaitingBackendsAsync() >= 1);
        List<Guid> free;
        await using (var probe = api.Db())
        {
            free = await probe.Database.SqlQuery<Guid>(
                $"SELECT id AS \"Value\" FROM machine_grants WHERE id IN ({first.Id}, {last.Id}) FOR UPDATE SKIP LOCKED").ToListAsync();
        }

        await hold.CommitAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await revoke).StatusCode);
        Assert.Equal([last.Id], free);
    }
}
