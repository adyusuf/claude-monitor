using System.Net;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>Agent actions are personal data (ADR-0005, "Agent actions from the web"): kept for the workspace's retention, exported with the account, their result text emptied on deletion.</summary>
[Collection(ApiGroup.Name)]
public sealed class AgentActionPrivacyTests(ApiFactory api)
{
    private const string Marker = "/home/person/ACTION-RESULT-MARKER";

    private DateTimeOffset Now => api.Clock.GetUtcNow();

    private async Task<bool> ExistsAsync(Guid id)
    {
        await using var db = api.Db();
        return await db.AgentActions.AnyAsync(a => a.Id == id);
    }

    private static async Task<JsonElement> ExportAsync(TestUser user)
    {
        var response = await user.Http.GetAsync("/api/me/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    /// <summary>A finished status action with a result, a failed update check, and a restart still queued.</summary>
    private async Task<(RemoteTeam Team, Guid Finished, Guid Failed, Guid Queued)> BuildAsync()
    {
        var team = await RemoteKit.TeamAsync(api);
        var finished = await ActionKit.CreateAsync(team.Owner, team.TargetId, AgentActionKinds.Status);
        await ActionKit.FinishAsync(team.Target, finished.Id, AgentActionStatuses.Succeeded, Marker);
        api.Clock.Advance(TimeSpan.FromSeconds(1));
        var failed = await ActionKit.CreateAsync(team.Owner, team.TargetId, AgentActionKinds.UpdateCheck);
        await ActionKit.FinishAsync(team.Target, failed.Id, AgentActionStatuses.Failed, Marker + "-failed");
        api.Clock.Advance(TimeSpan.FromSeconds(1));
        var queued = await ActionKit.CreateAsync(team.Owner, team.TargetId, AgentActionKinds.Restart);
        return (team, finished.Id, failed.Id, queued.Id);
    }

    // ---- retention ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Housekeeping_deletes_only_finished_actions_older_than_the_workspaces_retention()
    {
        var team = await RemoteKit.TeamAsync(api);
        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/workspaces/{team.WorkspaceId}/settings", new { retentionDays = 1 })).EnsureSuccessStatusCode();
        var longAgo = Now.AddDays(-3);
        var oldSucceeded = await ActionKit.InsertAsync(api, team, AgentActionKinds.Status, AgentActionStatuses.Succeeded, longAgo, Now.AddDays(-2), "old");
        var oldFailed = await ActionKit.InsertAsync(api, team, AgentActionKinds.UpdateCheck, AgentActionStatuses.Failed, longAgo, Now.AddDays(-1).AddMinutes(-1), "old");
        var oldExpired = await ActionKit.InsertAsync(api, team, AgentActionKinds.Restart, AgentActionStatuses.Expired, longAgo, Now.AddDays(-2));
        var justInside = await ActionKit.InsertAsync(api, team, AgentActionKinds.Update, AgentActionStatuses.Succeeded, longAgo, Now.AddDays(-1).AddMinutes(1), "recent");
        var recent = await ActionKit.InsertAsync(api, team, AgentActionKinds.Status, AgentActionStatuses.Succeeded, Now.AddHours(-2), Now.AddHours(-1), "recent");
        var oldDelivered = await ActionKit.InsertAsync(api, team, AgentActionKinds.Update, AgentActionStatuses.Delivered, longAgo, null);
        var oldQueued = await ActionKit.InsertAsync(api, team, AgentActionKinds.Restart, AgentActionStatuses.Queued, longAgo, null);

        await RemoteKit.HousekeepAsync(api);

        Assert.Equal(new[] { false, false, false }, new[] { await ExistsAsync(oldSucceeded), await ExistsAsync(oldFailed), await ExistsAsync(oldExpired) });
        Assert.Equal(new[] { true, true, true, true }, new[] { await ExistsAsync(justInside), await ExistsAsync(recent), await ExistsAsync(oldDelivered), await ExistsAsync(oldQueued) });
    }

    [Fact]
    public async Task Each_workspaces_own_retention_decides_so_the_same_age_stays_in_one_and_goes_from_another()
    {
        var brief = await RemoteKit.TeamAsync(api);
        (await brief.Admin.SendAsync(HttpMethod.Put, $"/api/workspaces/{brief.WorkspaceId}/settings", new { retentionDays = 1 })).EnsureSuccessStatusCode();
        var standard = await RemoteKit.TeamAsync(api);
        var goes = await ActionKit.InsertAsync(api, brief, AgentActionKinds.Status, AgentActionStatuses.Succeeded, Now.AddDays(-3), Now.AddDays(-2));
        var stays = await ActionKit.InsertAsync(api, standard, AgentActionKinds.Status, AgentActionStatuses.Succeeded, Now.AddDays(-3), Now.AddDays(-2));

        await RemoteKit.HousekeepAsync(api);

        Assert.False(await ExistsAsync(goes));
        Assert.True(await ExistsAsync(stays));
    }

    // ---- export -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_export_holds_the_actions_the_user_asked_for_with_their_results_and_nobody_elses()
    {
        var w = await BuildAsync();
        var bystander = await BuildAsync();

        var owner = await ExportAsync(w.Team.Owner);

        var rows = owner.GetProperty("agentActions").EnumerateArray().ToList();
        Assert.Equal(new[] { w.Finished, w.Failed, w.Queued }, rows.Select(r => r.GetProperty("id").GetGuid()).ToArray()); // oldest first
        var finished = rows[0];
        Assert.Equal((w.Team.TargetId, AgentActionKinds.Status, AgentActionStatuses.Succeeded, Marker),
            (finished.GetProperty("agentId").GetGuid(), finished.GetProperty("kind").GetString(), finished.GetProperty("status").GetString(), finished.GetProperty("result").GetString()));
        Assert.NotEqual(JsonValueKind.Null, finished.GetProperty("finishedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, finished.GetProperty("createdAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[2].GetProperty("finishedAt").ValueKind);
        Assert.DoesNotContain(bystander.Finished, rows.Select(r => r.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task The_export_of_a_member_who_asked_for_nothing_holds_no_actions_and_none_of_the_owners_results()
    {
        var w = await BuildAsync();

        foreach (var user in new[] { w.Team.Admin, w.Team.Other })
        {
            var export = await ExportAsync(user);
            Assert.Empty(export.GetProperty("agentActions").EnumerateArray());
            Assert.DoesNotContain("ACTION-RESULT-MARKER", export.ToString(), StringComparison.Ordinal);
        }
    }

    // ---- account deletion ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_the_owners_account_empties_every_result_and_keeps_the_rows_and_leaves_other_peoples_results_alone()
    {
        var w = await BuildAsync();
        var bystander = await BuildAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await w.Team.Owner.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).StatusCode);

        var rows = await ActionKit.RowsAsync(api, w.Team.TargetId);
        Assert.Equal(new[] { w.Finished, w.Failed, w.Queued }, rows.Select(r => r.Id).ToArray());
        Assert.All(rows, r => Assert.Null(r.Result));
        Assert.Equal(new[] { AgentActionKinds.Status, AgentActionKinds.UpdateCheck, AgentActionKinds.Restart }, rows.Select(r => r.Kind).ToArray());
        Assert.Equal(AgentActionStatuses.Succeeded, rows[0].Status);
        Assert.Equal(AgentActionStatuses.Failed, rows[1].Status);
        var kept = await ActionKit.RowAsync(api, bystander.Finished);
        Assert.Equal(Marker, kept.Result);
    }
}
