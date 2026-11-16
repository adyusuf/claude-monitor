using System.Text.Json;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>F-225 for a project's member list: the e-mail address of each member is shown by the reader's workspace role, to admins and owners only.</summary>
[Collection(ApiGroup.Name)]
public sealed class ProjectMemberEmailVisibilityTests(ApiFactory api)
{
    [Theory]
    [InlineData("owner", true)]
    [InlineData("admin", true)]
    [InlineData("member", false)]
    [InlineData("viewer", false)]
    public async Task The_project_member_list_carries_every_address_to_admins_and_owners_and_none_below(string role, bool sees)
    {
        var owner = await api.NewClient().SignedUpAsync("pmemail-owner-" + role);
        var other = await RemoteKit.MemberAsync(api, owner, "pmemail-other-" + role, "member");
        var reader = role == "owner" ? owner : await RemoteKit.MemberAsync(api, owner, "pmemail-reader-" + role, role);
        var project = (await ProjectKit.CreateProjectAsync(owner, "Member list " + role)).GetProperty("id").GetGuid();
        foreach (var person in new[] { other, reader }.Where(p => p.Id != owner.Id))
        {
            (await owner.SendAsync(HttpMethod.Put, $"/api/projects/{project}/members/{person.Id}", new { role = "contributor" })).EnsureSuccessStatusCode();
        }

        var rows = (await reader.GetJsonAsync($"/api/projects/{project}/members")).EnumerateArray()
            .ToDictionary(m => m.GetProperty("userId").GetGuid(), m => m.GetProperty("email").ValueKind == JsonValueKind.Null ? null : m.GetProperty("email").GetString());

        Assert.Equal(sees ? other.Email : null, rows[other.Id], StringComparer.OrdinalIgnoreCase);
        if (reader.Id != owner.Id) Assert.Equal(sees ? reader.Email : null, rows[reader.Id], StringComparer.OrdinalIgnoreCase); // the owner of a project is no row of its own
    }
}
