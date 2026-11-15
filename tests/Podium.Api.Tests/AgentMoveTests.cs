using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>What a move of an agent (<c>PATCH /agents/{id}</c>) resets, what it keeps, and where it is audited.</summary>
[Collection(ApiGroup.Name)]
public sealed class AgentMoveTests(ApiFactory api)
{
    private static Task<HttpResponseMessage> MoveAsync(TestUser user, Guid agent, Guid workspace) =>
        user.SendAsync(HttpMethod.Patch, $"/api/agents/{agent}", new { workspaceId = workspace });

    [Fact]
    public async Task Moving_an_agent_to_its_own_workspace_keeps_an_admin_set_role_and_its_open_runs()
    {
        var team = await RemoteKit.TeamAsync(api);
        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/role", new { environment = "production", function = "database" }))
            .EnsureSuccessStatusCode();
        var created = await RemoteKit.CreateAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()));

        Assert.Equal(HttpStatusCode.NoContent, (await MoveAsync(team.Owner, team.TargetId, team.WorkspaceId)).StatusCode);

        await using var db = api.Db();
        var row = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId);
        Assert.Equal(("production", "database", team.Admin.Id), (row.RoleEnvironment, row.RoleFunction, row.RoleSetBy));
        Assert.Equal(team.WorkspaceId, row.WorkspaceId);
        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, created.Id));
        Assert.False(await db.AuditEvents.AnyAsync(a => a.Action == AuditActions.AgentMoved && a.TargetId == team.TargetId.ToString()));
    }

    private static async Task<JsonElement> MovedRowAsync(TestUser admin, Guid workspace, Guid agent) =>
        (await admin.GetJsonAsync($"/api/workspaces/{workspace}/audit")).GetProperty("items").EnumerateArray()
            .Single(r => r.GetProperty("action").GetString() == AuditActions.AgentMoved && r.GetProperty("targetId").GetString() == agent.ToString());

    [Fact]
    public async Task The_move_is_audited_in_the_old_workspace_too()
    {
        var team = await RemoteKit.TeamAsync(api);

        Assert.Equal(HttpStatusCode.NoContent, (await MoveAsync(team.Owner, team.TargetId, team.Owner.WorkspaceId)).StatusCode);

        foreach (var (reader, workspace) in new[] { (team.Admin, team.WorkspaceId), (team.Owner, team.Owner.WorkspaceId) })
        {
            var detail = (await MovedRowAsync(reader, workspace, team.TargetId)).GetProperty("detail");
            Assert.Equal((team.WorkspaceId, team.Owner.WorkspaceId), (detail.GetProperty("from").GetGuid(), detail.GetProperty("to").GetGuid()));
        }
    }

    [Fact]
    public async Task Moving_an_agent_clears_the_tags_the_note_and_the_listening_services()
    {
        var team = await RemoteKit.TeamAsync(api);
        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/tags", new { tags = new Dictionary<string, string> { ["env"] = "lab" } })).EnsureSuccessStatusCode();
        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/note", new { note = "restart the queue first" })).EnsureSuccessStatusCode();
        (await team.Owner.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/services", new { share = true })).EnsureSuccessStatusCode();
        var profile = new AgentProfile(ExecLevels.Argv, false, null, null, 2, Services: [new ServiceInfo(5432, "tcp", "postgres")]);
        (await team.Target.Http.PutAsJsonAsync("/api/agent/profile", profile, TestUser.Json)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await MoveAsync(team.Owner, team.TargetId, team.Owner.WorkspaceId)).StatusCode);

        await using var db = api.Db();
        var row = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId);
        Assert.Equal((null, null, null, null), (row.Tags, row.TagsSetBy, row.AdminNote, row.AdminNoteSetBy));
        Assert.Equal((false, null, null), (row.ShareServices, row.Services, row.ServicesAt));
    }
}
