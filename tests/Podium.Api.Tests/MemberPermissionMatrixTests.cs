using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>
/// Who may manage a workspace's people: the role of the caller against what they try (WorkspaceEndpoints, InvitationEndpoints).
/// Every row runs in its own workspace with an owner, a plain member as the target and one open invitation.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class MemberPermissionMatrixTests(ApiFactory api)
{
    private sealed record Workspace(TestUser Owner, TestUser Target, Guid InvitationId);

    private async Task<Workspace> NewWorkspaceAsync()
    {
        var owner = await api.NewClient().SignedUpAsync("pm-owner");
        var target = await RemoteKit.MemberAsync(api, owner, "pm-target");
        var created = await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = Emails.New("pm-open") });
        var invitation = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return new Workspace(owner, target, invitation);
    }

    private static string Fill(string template, Workspace ws) => template
        .Replace("{ws}", ws.Owner.WorkspaceId.ToString(), StringComparison.Ordinal)
        .Replace("{target}", ws.Target.Id.ToString(), StringComparison.Ordinal)
        .Replace("{invitation}", ws.InvitationId.ToString(), StringComparison.Ordinal)
        .Replace("{email}", Emails.New("pm-new"), StringComparison.Ordinal);

    private static JsonElement? Body(string? json, Workspace ws) => json is null ? null : JsonDocument.Parse(Fill(json, ws)).RootElement;

    /// <summary>The members with their roles and the number of open invitations, as the owner sees them.</summary>
    private static async Task<string> SnapshotAsync(Workspace ws)
    {
        var members = await ws.Owner.GetJsonAsync($"/api/workspaces/{ws.Owner.WorkspaceId}/members");
        var invitations = await ws.Owner.GetJsonAsync($"/api/workspaces/{ws.Owner.WorkspaceId}/invitations");
        var roles = members.EnumerateArray().Select(m => m.GetProperty("userId").GetGuid() + ":" + m.GetProperty("role").GetString()).Order();
        return string.Join(",", roles) + "|" + invitations.GetArrayLength();
    }

    [Theory]
    [InlineData("member", "POST", "/api/workspaces/{ws}/invitations", """{"email":"{email}","role":"member"}""")]
    [InlineData("member", "GET", "/api/workspaces/{ws}/invitations", null)]
    [InlineData("member", "DELETE", "/api/workspaces/{ws}/invitations/{invitation}", null)]
    [InlineData("member", "PATCH", "/api/workspaces/{ws}/members/{target}", """{"role":"admin"}""")]
    [InlineData("member", "DELETE", "/api/workspaces/{ws}/members/{target}", null)]
    [InlineData("viewer", "POST", "/api/workspaces/{ws}/invitations", """{"email":"{email}","role":"viewer"}""")]
    [InlineData("viewer", "GET", "/api/workspaces/{ws}/invitations", null)]
    [InlineData("viewer", "DELETE", "/api/workspaces/{ws}/invitations/{invitation}", null)]
    [InlineData("viewer", "PATCH", "/api/workspaces/{ws}/members/{target}", """{"role":"admin"}""")]
    [InlineData("viewer", "DELETE", "/api/workspaces/{ws}/members/{target}", null)]
    public async Task A_member_or_viewer_cannot_manage_people_and_nothing_changes(string role, string method, string url, string? body)
    {
        var ws = await NewWorkspaceAsync();
        var actor = await RemoteKit.MemberAsync(api, ws.Owner, "pm-actor", role);
        var before = await SnapshotAsync(ws);

        var response = await actor.SendAsync(new HttpMethod(method), Fill(url, ws), Body(body, ws));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(ws));
    }

    [Theory]
    [InlineData("POST", "/api/workspaces/{ws}/invitations", """{"email":"{email}","role":"member"}""", HttpStatusCode.Created)]
    [InlineData("GET", "/api/workspaces/{ws}/invitations", null, HttpStatusCode.OK)]
    [InlineData("DELETE", "/api/workspaces/{ws}/invitations/{invitation}", null, HttpStatusCode.NoContent)]
    [InlineData("PATCH", "/api/workspaces/{ws}/members/{target}", """{"role":"viewer"}""", HttpStatusCode.NoContent)]
    [InlineData("DELETE", "/api/workspaces/{ws}/members/{target}", null, HttpStatusCode.NoContent)]
    public async Task An_admin_may_manage_people_below_the_owner(string method, string url, string? body, HttpStatusCode expected)
    {
        var ws = await NewWorkspaceAsync();
        var admin = await RemoteKit.MemberAsync(api, ws.Owner, "pm-admin", "admin");

        var response = await admin.SendAsync(new HttpMethod(method), Fill(url, ws), Body(body, ws));

        Assert.Equal(expected, response.StatusCode);
    }

    private static async Task<string> RoleOfAsync(TestUser viewer, Guid workspaceId, Guid userId)
    {
        var members = await viewer.GetJsonAsync($"/api/workspaces/{workspaceId}/members");
        return members.EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == userId).GetProperty("role").GetString()!;
    }

    [Fact]
    public async Task An_admin_cannot_make_a_member_an_owner()
    {
        var ws = await NewWorkspaceAsync();
        var admin = await RemoteKit.MemberAsync(api, ws.Owner, "pm-admin", "admin");

        var response = await admin.SendAsync(HttpMethod.Patch, $"/api/workspaces/{ws.Owner.WorkspaceId}/members/{ws.Target.Id}", new { role = "owner" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("member", await RoleOfAsync(ws.Owner, ws.Owner.WorkspaceId, ws.Target.Id));
    }

    [Fact]
    public async Task An_admin_cannot_make_themselves_an_owner()
    {
        var ws = await NewWorkspaceAsync();
        var admin = await RemoteKit.MemberAsync(api, ws.Owner, "pm-self-up", "admin");

        var response = await admin.SendAsync(HttpMethod.Patch, $"/api/workspaces/{ws.Owner.WorkspaceId}/members/{admin.Id}", new { role = "owner" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("admin", await RoleOfAsync(ws.Owner, ws.Owner.WorkspaceId, admin.Id));
    }

    [Fact]
    public async Task An_admin_can_step_down_to_member_and_then_loses_the_admin_calls()
    {
        var ws = await NewWorkspaceAsync();
        var admin = await RemoteKit.MemberAsync(api, ws.Owner, "pm-step-down", "admin");

        var response = await admin.SendAsync(HttpMethod.Patch, $"/api/workspaces/{ws.Owner.WorkspaceId}/members/{admin.Id}", new { role = "member" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("member", await RoleOfAsync(ws.Owner, ws.Owner.WorkspaceId, admin.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Http.GetAsync($"/api/workspaces/{ws.Owner.WorkspaceId}/invitations")).StatusCode);
    }

    [Fact]
    public async Task An_admin_can_remove_another_admin_who_then_loses_the_workspace()
    {
        var ws = await NewWorkspaceAsync();
        var admin = await RemoteKit.MemberAsync(api, ws.Owner, "pm-remover", "admin");
        var other = await RemoteKit.MemberAsync(api, ws.Owner, "pm-removed", "admin");

        var response = await admin.SendAsync(HttpMethod.Delete, $"/api/workspaces/{ws.Owner.WorkspaceId}/members/{other.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync($"/api/workspaces/{ws.Owner.WorkspaceId}")).StatusCode);
    }

    [Fact]
    public async Task Setting_the_role_a_member_already_has_succeeds_and_changes_nothing()
    {
        var ws = await NewWorkspaceAsync();

        var response = await ws.Owner.SendAsync(HttpMethod.Patch, $"/api/workspaces/{ws.Owner.WorkspaceId}/members/{ws.Target.Id}", new { role = "member" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("member", await RoleOfAsync(ws.Owner, ws.Owner.WorkspaceId, ws.Target.Id));
    }
}
