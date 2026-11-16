using System.Net;
using System.Net.Http.Json;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// A policy the web took back (F-304, decided to stay as it is): the by-key answer is 404 ("no policy covers this repository"), yet the machines are
/// still sent the tombstone with its repositories, so the agent applies the default to them even with the enforcement switch off. The 404 is not "no limit".
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class AccessClearedPolicyTests(ApiFactory api)
{
    private const string Shop = "git:git.example.invalid/team/shop"; // the repository of ProjectKit.Application
    private static readonly AccessOsRules None = new([], [], []);

    [Fact]
    public async Task A_cleared_policy_answers_404_by_key_while_the_machines_still_get_the_tombstone_that_makes_the_default_apply_with_the_switch_off()
    {
        var admin = await api.NewClient().SignedUpAsync("apc-cleared");
        var agent = await admin.ConnectAgentAsync();
        var project = (await ProjectKit.CreateProjectAsync(admin, "Cleared policy")).GetProperty("id").GetGuid();
        await ProjectKit.CreateApplicationAsync(admin, project, "Shop");
        var live = AccessPolicyRules.Canonical(new AccessPolicyDocument(AccessPolicyRules.Format, [Shop], false, new AccessOsRules(["/srv/app"], [], []), None, None, false, false));
        var cleared = AccessPolicyRules.Canonical(new AccessPolicyDocument(AccessPolicyRules.Format, [Shop], false, None, None, None, false, true));
        string ByKey() => $"/api/workspaces/{admin.WorkspaceId}/access-policies/by-key?projectKey={Uri.EscapeDataString(Shop)}";
        (await admin.SendAsync(HttpMethod.Put, $"/api/projects/{project}/access-policy", new AccessPolicySave(0, live, null, null, null))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await admin.Http.GetAsync(ByKey())).StatusCode);

        (await admin.SendAsync(HttpMethod.Put, $"/api/projects/{project}/access-policy", new AccessPolicySave(1, cleared, null, null, null))).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.Http.GetAsync(ByKey())).StatusCode);
        var set = await agent.Http.GetFromJsonAsync<AgentAccessPolicies>("/api/agent/access-policies", TestUser.Json);
        var tombstone = Assert.Single(set!.Policies, p => p.ProjectId == project);
        Assert.True(tombstone.Document.Cleared);
        Assert.Equal([Shop], tombstone.Document.ProjectKeys);
        var workspace = await admin.GetJsonAsync($"/api/workspaces/{admin.WorkspaceId}");
        Assert.False(workspace.GetProperty("settings").GetProperty("enforceAccessPolicies").GetBoolean());
    }
}
