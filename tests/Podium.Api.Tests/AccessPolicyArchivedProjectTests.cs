using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.AccessPolicies;
using Podium.Api.Remote;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// F-305: a policy on an archived project stays allowed. Archiving does not release the policy's keys and only the workspace's admins can clear it,
/// so they must still be able to save, change and clear it there, while the project page keeps its other cards read-only. This pins the decision.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class AccessPolicyArchivedProjectTests(ApiFactory api)
{
    private const string Shop = "git:git.example.invalid/team/shop"; // the repository of ProjectKit.Application
    private static readonly AccessOsRules None = new([], [], []);

    private static AccessPolicyDocument Doc(string folder, bool cleared = false) =>
        AccessPolicyRules.Canonical(new AccessPolicyDocument(AccessPolicyRules.Format, [Shop], false, cleared ? None : new AccessOsRules([folder], ["/srv/app/secret"], []),
            None, None, false, cleared));

    private static Task<HttpResponseMessage> SaveAsync(TestUser user, Guid projectId, int expected, AccessPolicyDocument document) =>
        user.SendAsync(HttpMethod.Put, $"/api/projects/{projectId}/access-policy", new AccessPolicySave(expected, document, null, null, null));

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task An_admin_saves_changes_and_clears_the_policy_of_an_archived_project_and_everyone_still_reads_it()
    {
        var admin = await api.NewClient().SignedUpAsync("apa-admin");
        var member = await RemoteKit.MemberAsync(api, admin, "apa-member");
        var project = (await ProjectKit.CreateProjectAsync(admin, "Policy archived")).GetProperty("id").GetGuid();
        await ProjectKit.CreateApplicationAsync(admin, project, "Shop");
        await BodyAsync(await SaveAsync(admin, project, 0, Doc("/srv/app")));
        (await admin.SendAsync(HttpMethod.Delete, $"/api/projects/{project}")).EnsureSuccessStatusCode();
        Assert.Equal("archived", (await admin.GetJsonAsync($"/api/projects/{project}")).GetProperty("status").GetString());

        var changed = await BodyAsync(await SaveAsync(admin, project, 1, Doc("/srv/other")));
        var read = await member.GetJsonAsync($"/api/projects/{project}/access-policy");
        var cleared = await BodyAsync(await SaveAsync(admin, project, 2, Doc("/srv/app", cleared: true)));

        Assert.Equal(2, changed.GetProperty("version").GetInt32());
        Assert.Equal(["/srv/other"], read.GetProperty("document").GetProperty("linux").GetProperty("allowedFolders").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(3, cleared.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task A_member_who_is_no_admin_still_cannot_save_on_an_archived_project()
    {
        var admin = await api.NewClient().SignedUpAsync("apa-deny-admin");
        var member = await RemoteKit.MemberAsync(api, admin, "apa-deny-member");
        var project = (await ProjectKit.CreateProjectAsync(admin, "Policy archived denied")).GetProperty("id").GetGuid();
        await ProjectKit.CreateApplicationAsync(admin, project, "Shop");
        (await admin.SendAsync(HttpMethod.Delete, $"/api/projects/{project}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await SaveAsync(member, project, 0, Doc("/srv/app"))).StatusCode);
    }
}
