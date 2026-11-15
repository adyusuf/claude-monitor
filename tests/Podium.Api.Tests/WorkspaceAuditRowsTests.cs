using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// The workspace audit log as an admin reads it: one row per workspace and people change, with the actor, the target and the detail
/// (the access-enforcement row is in <see cref="AccessEnforcementTests"/>).
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class WorkspaceAuditRowsTests(ApiFactory api)
{
    private static async Task<JsonElement> RowAsync(TestUser reader, Guid workspaceId, string action)
    {
        var page = await reader.GetJsonAsync($"/api/workspaces/{workspaceId}/audit?limit=100");
        return page.GetProperty("items").EnumerateArray().Single(a => a.GetProperty("action").GetString() == action);
    }

    private static async Task<Guid> InviteAsync(TestUser owner, TestUser invitee, string role)
    {
        var response = await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = invitee.Email, role });
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Creating_a_workspace_writes_a_workspace_created_row_for_its_creator()
    {
        var user = await api.NewClient().SignedUpAsync("aud-create");
        var created = await user.PostAsync("/api/workspaces", new { name = "Audit Team" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var row = await RowAsync(user, id, AuditActions.WorkspaceCreated);

        Assert.Equal(user.Id, row.GetProperty("actorUserId").GetGuid());
    }

    [Fact]
    public async Task Renaming_a_workspace_writes_a_workspace_renamed_row_with_before_and_after()
    {
        var owner = await api.NewClient().SignedUpAsync("aud-rename");
        var before = (await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}")).GetProperty("name").GetString();
        (await owner.SendAsync(HttpMethod.Patch, $"/api/workspaces/{owner.WorkspaceId}", new { name = "Renamed Team" })).EnsureSuccessStatusCode();

        var row = await RowAsync(owner, owner.WorkspaceId, AuditActions.WorkspaceRenamed);

        var detail = row.GetProperty("detail");
        Assert.Equal((owner.Id, before, "Renamed Team"),
            (row.GetProperty("actorUserId").GetGuid(), detail.GetProperty("before").GetString(), detail.GetProperty("after").GetString()));
    }

    [Fact]
    public async Task Inviting_writes_an_invitation_created_row_with_the_invited_role()
    {
        var owner = await api.NewClient().SignedUpAsync("aud-inv-owner");
        var invitee = await api.NewClient().SignedUpAsync("aud-inv-invitee");
        var invitation = await InviteAsync(owner, invitee, "admin");

        var row = await RowAsync(owner, owner.WorkspaceId, AuditActions.InvitationCreated);

        Assert.Equal((owner.Id, "invitation", invitation.ToString(), "admin"),
            (row.GetProperty("actorUserId").GetGuid(), row.GetProperty("targetType").GetString(), row.GetProperty("targetId").GetString(),
                row.GetProperty("detail").GetProperty("role").GetString()));
    }

    [Fact]
    public async Task Accepting_writes_an_invitation_accepted_row_for_the_one_who_accepted()
    {
        var owner = await api.NewClient().SignedUpAsync("aud-acc-owner");
        var invitee = await api.NewClient().SignedUpAsync("aud-acc-invitee");
        var invitation = await InviteAsync(owner, invitee, "member");
        (await invitee.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(invitee.Email) })).EnsureSuccessStatusCode();

        var row = await RowAsync(owner, owner.WorkspaceId, AuditActions.InvitationAccepted);

        Assert.Equal((invitee.Id, "invitation", invitation.ToString()),
            (row.GetProperty("actorUserId").GetGuid(), row.GetProperty("targetType").GetString(), row.GetProperty("targetId").GetString()));
    }

    [Fact]
    public async Task Revoking_writes_an_invitation_revoked_row_for_the_admin_who_revoked()
    {
        var owner = await api.NewClient().SignedUpAsync("aud-rev-owner");
        var invitee = await api.NewClient().SignedUpAsync("aud-rev-invitee");
        var invitation = await InviteAsync(owner, invitee, "member");
        (await owner.SendAsync(HttpMethod.Delete, $"/api/workspaces/{owner.WorkspaceId}/invitations/{invitation}")).EnsureSuccessStatusCode();

        var row = await RowAsync(owner, owner.WorkspaceId, AuditActions.InvitationRevoked);

        Assert.Equal((owner.Id, "invitation", invitation.ToString()),
            (row.GetProperty("actorUserId").GetGuid(), row.GetProperty("targetType").GetString(), row.GetProperty("targetId").GetString()));
    }

    [Fact]
    public async Task Changing_a_role_writes_a_role_changed_row_with_before_and_after()
    {
        var owner = await api.NewClient().SignedUpAsync("aud-role-owner");
        var member = await RemoteKit.MemberAsync(api, owner, "aud-role-member");
        (await owner.SendAsync(HttpMethod.Patch, $"/api/workspaces/{owner.WorkspaceId}/members/{member.Id}", new { role = "admin" })).EnsureSuccessStatusCode();

        var row = await RowAsync(owner, owner.WorkspaceId, AuditActions.RoleChanged);

        var detail = row.GetProperty("detail");
        Assert.Equal((owner.Id, "user", member.Id.ToString(), "member", "admin"),
            (row.GetProperty("actorUserId").GetGuid(), row.GetProperty("targetType").GetString(), row.GetProperty("targetId").GetString(),
                detail.GetProperty("before").GetString(), detail.GetProperty("after").GetString()));
    }

    [Fact]
    public async Task Setting_the_role_a_member_already_has_writes_no_role_changed_row()
    {
        var owner = await api.NewClient().SignedUpAsync("aud-same-owner");
        var member = await RemoteKit.MemberAsync(api, owner, "aud-same-member");

        var response = await owner.SendAsync(HttpMethod.Patch, $"/api/workspaces/{owner.WorkspaceId}/members/{member.Id}", new { role = "member" });

        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        var page = await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/audit?limit=100");
        Assert.DoesNotContain(page.GetProperty("items").EnumerateArray(), a => a.GetProperty("action").GetString() == AuditActions.RoleChanged);
    }

    [Fact]
    public async Task Removing_a_member_writes_a_member_removed_row_naming_the_member()
    {
        var owner = await api.NewClient().SignedUpAsync("aud-rm-owner");
        var member = await RemoteKit.MemberAsync(api, owner, "aud-rm-member");
        (await owner.SendAsync(HttpMethod.Delete, $"/api/workspaces/{owner.WorkspaceId}/members/{member.Id}")).EnsureSuccessStatusCode();

        var row = await RowAsync(owner, owner.WorkspaceId, AuditActions.MemberRemoved);

        Assert.Equal((owner.Id, "user", member.Id.ToString()),
            (row.GetProperty("actorUserId").GetGuid(), row.GetProperty("targetType").GetString(), row.GetProperty("targetId").GetString()));
    }
}
