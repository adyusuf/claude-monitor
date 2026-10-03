using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;

namespace ClaudeMonitor.Api.Tests;

[Collection(ApiGroup.Name)]
public sealed class WorkspaceTests(ApiFactory api)
{
    private async Task<(TestUser Owner, TestUser Member)> TeamAsync(string memberRole = "member")
    {
        var owner = await api.NewClient().SignedUpAsync("owner");
        var member = await api.NewClient().SignedUpAsync("member");
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = member.Email, role = memberRole }))
            .EnsureSuccessStatusCode();
        (await member.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(member.Email) })).EnsureSuccessStatusCode();
        return (owner, member);
    }

    [Fact]
    public async Task Create_rename_and_read_a_workspace()
    {
        var user = await api.NewClient().SignedUpAsync("ws");
        var created = await user.PostAsync("/api/workspaces", new { name = "Team Çiçek" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await user.SendAsync(HttpMethod.Patch, $"/api/workspaces/{id}", new { name = "Renamed" })).StatusCode);
        var ws = await user.GetJsonAsync($"/api/workspaces/{id}");
        Assert.Equal("Renamed", ws.GetProperty("name").GetString());
        Assert.Equal("owner", ws.GetProperty("role").GetString());
        Assert.Equal(90, ws.GetProperty("settings").GetProperty("retentionDays").GetInt32());
        Assert.True(ws.GetProperty("settings").GetProperty("maskSecrets").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsync("/api/workspaces", new { name = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.SendAsync(HttpMethod.Patch, $"/api/workspaces/{id}", new { name = "" })).StatusCode);
    }

    [Fact]
    public async Task A_workspace_one_does_not_belong_to_does_not_exist()
    {
        var a = await api.NewClient().SignedUpAsync("a");
        var b = await api.NewClient().SignedUpAsync("b");
        Assert.Equal(HttpStatusCode.NotFound, (await b.Http.GetAsync($"/api/workspaces/{a.WorkspaceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Http.GetAsync($"/api/workspaces/{a.WorkspaceId}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Http.GetAsync($"/api/workspaces/{a.WorkspaceId}/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Http.GetAsync($"/api/workspaces/{a.WorkspaceId}/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/workspaces/{a.WorkspaceId}/invitations", new { email = b.Email })).StatusCode);
    }

    [Fact]
    public async Task Settings_are_validated_audited_and_admin_only()
    {
        var (owner, member) = await TeamAsync();
        var url = $"/api/workspaces/{owner.WorkspaceId}/settings";
        Assert.Equal(HttpStatusCode.NotFound, (await member.SendAsync(HttpMethod.Put, url, new { retentionDays = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.SendAsync(HttpMethod.Put, url, new { retentionDays = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.SendAsync(HttpMethod.Put, url, new { eventMaxBytes = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.SendAsync(HttpMethod.Put, url, new { retentionDays = 30, maskSecrets = false, eventMaxBytes = 4096 })).StatusCode);
        var s = (await member.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}")).GetProperty("settings");
        Assert.Equal(30, s.GetProperty("retentionDays").GetInt32());
        Assert.False(s.GetProperty("maskSecrets").GetBoolean());
        var audit = await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/audit");
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), a => a.GetProperty("action").GetString() == "workspace.settings_changed");
        Assert.Equal(HttpStatusCode.NotFound, (await member.Http.GetAsync($"/api/workspaces/{owner.WorkspaceId}/audit")).StatusCode);
    }

    [Fact]
    public async Task Invitations_add_members_with_the_invited_role()
    {
        var (owner, member) = await TeamAsync("viewer");
        var members = await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/members");
        Assert.Equal(2, members.GetArrayLength());
        Assert.Contains(members.EnumerateArray(), m => m.GetProperty("userId").GetGuid() == member.Id && m.GetProperty("role").GetString() == "viewer");
        var me = await member.GetJsonAsync("/api/me");
        Assert.Equal(2, me.GetProperty("workspaces").GetArrayLength());
    }

    [Fact]
    public async Task An_invitation_is_only_for_its_address_works_once_and_can_be_revoked()
    {
        var owner = await api.NewClient().SignedUpAsync("inv-owner");
        var invited = await api.NewClient().SignedUpAsync("inv-target");
        var other = await api.NewClient().SignedUpAsync("inv-other");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = invited.Email, role = "god" })).StatusCode);
        var created = await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = invited.Email.ToUpperInvariant() });
        var token = api.Mail.TokenFor(invited.Email.ToUpperInvariant());
        Assert.Equal(HttpStatusCode.BadRequest, (await other.PostAsync("/api/invitations/accept", new { token })).StatusCode);
        var list = await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/invitations");
        Assert.Equal(1, list.GetArrayLength());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Delete, $"/api/workspaces/{owner.WorkspaceId}/invitations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.SendAsync(HttpMethod.Delete, $"/api/workspaces/{owner.WorkspaceId}/invitations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await invited.PostAsync("/api/invitations/accept", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await invited.PostAsync("/api/invitations/accept", new { token = "" })).StatusCode);
    }

    [Fact]
    public async Task An_admin_cannot_invite_above_their_role_or_touch_owners()
    {
        var (owner, admin) = await TeamAsync("admin");
        var third = await api.NewClient().SignedUpAsync("third");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = third.Email, role = "owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.SendAsync(HttpMethod.Patch, $"/api/workspaces/{owner.WorkspaceId}/members/{owner.Id}", new { role = "member" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.SendAsync(HttpMethod.Delete, $"/api/workspaces/{owner.WorkspaceId}/members/{owner.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.SendAsync(HttpMethod.Patch, $"/api/workspaces/{owner.WorkspaceId}/members/{admin.Id}", new { role = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.SendAsync(HttpMethod.Patch, $"/api/workspaces/{owner.WorkspaceId}/members/{third.Id}", new { role = "member" })).StatusCode);
    }

    [Fact]
    public async Task The_last_owner_cannot_leave_or_be_demoted_but_ownership_can_move()
    {
        var (owner, member) = await TeamAsync();
        var ws = owner.WorkspaceId;
        Assert.Equal(HttpStatusCode.BadRequest,
            (await owner.SendAsync(HttpMethod.Patch, $"/api/workspaces/{ws}/members/{owner.Id}", new { role = "admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.SendAsync(HttpMethod.Delete, $"/api/workspaces/{ws}/members/{owner.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.SendAsync(HttpMethod.Patch, $"/api/workspaces/{ws}/members/{member.Id}", new { role = "owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Delete, $"/api/workspaces/{ws}/members/{owner.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Http.GetAsync($"/api/workspaces/{ws}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.SendAsync(HttpMethod.Delete, $"/api/workspaces/{ws}/members/{owner.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_member_can_leave_and_a_removed_member_can_be_invited_back()
    {
        var (owner, member) = await TeamAsync();
        var ws = owner.WorkspaceId;
        Assert.Equal(HttpStatusCode.NotFound,
            (await member.SendAsync(HttpMethod.Delete, $"/api/workspaces/{ws}/members/{owner.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await member.SendAsync(HttpMethod.Delete, $"/api/workspaces/{ws}/members/{member.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.Http.GetAsync($"/api/workspaces/{ws}")).StatusCode);
        (await owner.PostAsync($"/api/workspaces/{ws}/invitations", new { email = member.Email, role = "admin" })).EnsureSuccessStatusCode();
        (await member.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(member.Email) })).EnsureSuccessStatusCode();
        Assert.Equal("admin", (await member.GetJsonAsync($"/api/workspaces/{ws}")).GetProperty("role").GetString());
    }
}
