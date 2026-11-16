using System.Net;
using Microsoft.EntityFrameworkCore;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// F-102 against a concurrent assign: removing a member, or lowering them below Member, and assigning an issue to them must not both succeed, or
/// an issue ends up assigned to someone who is no longer a worker of the workspace. The assign holds the person's membership row for its
/// save, the removal and the demotion lock that row before they count the person's issues, so the two serialise.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class MemberAssignRaceTests(ApiFactory api)
{
    public const string Remove = "remove";
    public const string Demote = "demote";
    public const string Create = "create";
    public const string Update = "update";

    private static FormattableString Change(string change, Guid workspaceId, Guid userId)
    {
        if (change == Remove) return $"UPDATE workspace_members SET removed_at = now() WHERE workspace_id = {workspaceId} AND user_id = {userId}";
        return $"UPDATE workspace_members SET role = 'viewer' WHERE workspace_id = {workspaceId} AND user_id = {userId}";
    }

    private static async Task<(HttpResponseMessage Response, Guid? Issue)> AssignAsync(IssueTeam t, string how, Guid issue)
    {
        if (how == Create)
        {
            var created = await t.Owner.PostAsync($"/api/projects/{t.Project}/issues", new { title = "Raced create", type = "story", assigneeUserId = t.Contributor.Id });
            return (created, null);
        }

        return (await t.Owner.SendAsync(HttpMethod.Put, $"/api/issues/{issue}", new { title = "Raced update", type = "story", assigneeUserId = t.Contributor.Id }), issue);
    }

    private async Task<int> AssignedCountAsync(IssueTeam t)
    {
        await using var db = api.Db();
        return await db.Issues.CountAsync(i => i.WorkspaceId == t.Owner.WorkspaceId && i.AssigneeUserId == t.Contributor.Id);
    }

    private async Task<WorkspaceMember> MemberAsync(IssueTeam t)
    {
        await using var db = api.Db();
        return await db.WorkspaceMembers.AsNoTracking().SingleAsync(m => m.WorkspaceId == t.Owner.WorkspaceId && m.UserId == t.Contributor.Id);
    }

    /// <summary>
    /// The removal or demotion has written the member row but not committed when the assign arrives (the test holds it so): the assign must wait
    /// for it and then refuse the person, instead of judging the member it read before and saving the issue.
    /// </summary>
    [Theory]
    [InlineData(Remove, Create)]
    [InlineData(Remove, Update)]
    [InlineData(Demote, Create)]
    [InlineData(Demote, Update)]
    public async Task An_assign_that_meets_an_uncommitted_removal_or_demotion_waits_for_it_and_refuses_the_person(string change, string how)
    {
        var t = await IssueKit.TeamAsync(api);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Unassigned");

        await using (var holder = api.Db())
        {
            await using var hold = await holder.Database.BeginTransactionAsync();
            await holder.Database.ExecuteSqlAsync(Change(change, t.Owner.WorkspaceId, t.Contributor.Id));
            var assign = Task.Run(() => AssignAsync(t, how, issue));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!assign.IsCompleted && await WaitingBackendsAsync() < 1) await Task.Delay(20, timeout.Token);
            await hold.CommitAsync();
            var (response, _) = await assign;
            Assert.Equal(("assigneeUserId", "not_a_member"), await ProjectKit.ProblemAsync(response));
        }

        Assert.Equal(0, await AssignedCountAsync(t));
    }

    /// <summary>Both start together (each blocked on the member row until both wait): exactly one wins, and never an issue of a non-worker.</summary>
    [Theory]
    [InlineData(Remove, Create)]
    [InlineData(Remove, Update)]
    [InlineData(Demote, Create)]
    [InlineData(Demote, Update)]
    public async Task A_removal_or_demotion_and_an_assign_at_once_never_leave_an_issue_with_a_non_worker(string change, string how)
    {
        var t = await IssueKit.TeamAsync(api);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, "Unassigned");
        var memberUrl = $"/api/workspaces/{t.Owner.WorkspaceId}/members/{t.Contributor.Id}";
        Func<Task<HttpResponseMessage>> changeCall = change == Remove
            ? () => t.Owner.SendAsync(HttpMethod.Delete, memberUrl)
            : () => t.Owner.SendAsync(HttpMethod.Patch, memberUrl, new { role = Roles.Viewer });

        var answers = await MemberLockHold.RunAsync(api, t.Owner.WorkspaceId, [t.Contributor.Id],
            changeCall, async () => (await AssignAsync(t, how, issue)).Response);

        var (changed, assigned) = (answers[0], answers[1]);
        var member = await MemberAsync(t);
        var stillWorker = member.RemovedAt is null && member.Role == Roles.Member;
        if (assigned.IsSuccessStatusCode)
        {
            // The assign went first: the removal or demotion then saw the issue and was refused.
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            Assert.True(stillWorker);
            Assert.Equal(1, await AssignedCountAsync(t));
        }
        else
        {
            Assert.Equal(("assigneeUserId", "not_a_member"), await ProjectKit.ProblemAsync(assigned));
            Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
            Assert.False(stillWorker);
            Assert.Equal(0, await AssignedCountAsync(t));
        }
    }

    private async Task<int> WaitingBackendsAsync()
    {
        await using var db = api.Db();
        return await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE wait_event_type = 'Lock'").SingleAsync();
    }
}
