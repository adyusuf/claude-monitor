using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>F-225: the member list shows each member's e-mail address to admins and owners only; viewers and members get null in its place.</summary>
[Collection(ApiGroup.Name)]
public sealed class MemberEmailVisibilityTests(ApiFactory api)
{
    private static async Task<Dictionary<Guid, string?>> EmailsSeenAsync(TestUser reader, Guid workspaceId)
    {
        var members = await reader.GetJsonAsync($"/api/workspaces/{workspaceId}/members");
        return members.EnumerateArray().ToDictionary(
            m => m.GetProperty("userId").GetGuid(),
            m => m.GetProperty("email").ValueKind == System.Text.Json.JsonValueKind.Null ? null : m.GetProperty("email").GetString());
    }

    [Theory]
    [InlineData("owner", true)]
    [InlineData("admin", true)]
    [InlineData("member", false)]
    [InlineData("viewer", false)]
    public async Task The_list_carries_every_address_to_admins_and_owners_and_none_below(string role, bool sees)
    {
        var owner = await api.NewClient().SignedUpAsync("memail-owner-" + role);
        var other = await RemoteKit.MemberAsync(api, owner, "memail-other-" + role, "member");
        var reader = role == "owner" ? owner : await RemoteKit.MemberAsync(api, owner, "memail-reader-" + role, role);

        var seen = await EmailsSeenAsync(reader, owner.WorkspaceId);

        Assert.Equal(sees ? owner.Email : null, seen[owner.Id], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(sees ? other.Email : null, seen[other.Id], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(sees ? reader.Email : null, seen[reader.Id], StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Names_and_roles_stay_visible_to_a_viewer()
    {
        var owner = await api.NewClient().SignedUpAsync("memail-names-owner", "Owner Name");
        var viewer = await RemoteKit.MemberAsync(api, owner, "memail-names-viewer", "viewer");

        var members = (await viewer.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/members")).EnumerateArray().ToList();

        Assert.Contains(members, m => m.GetProperty("displayName").GetString() == "Owner Name" && m.GetProperty("role").GetString() == "owner");
    }
}
