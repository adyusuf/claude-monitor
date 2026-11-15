using System.Net;
using System.Net.Http.Json;
using Podium.Api.AccessPolicies;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// What an agent receives of the access policies (ADR-0020 §4 rule 1): every policy of its workspace, tombstones included, under a stamp its settings
/// carry, which moves with every save and with the workspace switch; at most 200; only while its user is still a member.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class AccessPolicyDeliveryTests(ApiFactory api)
{
    private const string Route = "/api/agent/access-policies";
    private static readonly AccessOsRules None = new([], [], []);

    /// <summary>A policy row written as a save would have left it, for a project made here.</summary>
    private async Task<Guid> SeedAsync(TestUser admin, int version = 1, bool cleared = false, string? name = null)
    {
        var now = api.Clock.GetUtcNow();
        await using var db = api.Db();
        var project = new ManagedProject
        {
            WorkspaceId = admin.WorkspaceId, Name = name ?? "Delivered " + Guid.NewGuid().ToString("N"), OwnerUserId = admin.Id, CreatedBy = admin.Id,
            UpdatedBy = admin.Id, CreatedAt = now, UpdatedAt = now,
        };
        project.NameSearch = project.Name.ToLowerInvariant();
        var document = new AccessPolicyDocument(AccessPolicyRules.Format, ["git:git.example.invalid/team/" + project.Id.ToString("N")], false,
            cleared ? None : new AccessOsRules(["/srv/app"], [], []), None, None, false, cleared);
        db.Projects.Add(project);
        db.ProjectAccessPolicies.Add(new ProjectAccessPolicy
        {
            WorkspaceId = admin.WorkspaceId, ProjectId = project.Id, Version = version, Document = AccessPolicyStore.Write(document),
            DocumentHash = AccessPolicySigning.Hash(document), Cleared = cleared, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static async Task<AgentAccessPolicies> FetchAsync(TestAgent agent)
    {
        var response = await agent.Http.GetAsync(Route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AgentAccessPolicies>(TestUser.Json))!;
    }

    private static async Task<string?> SettingsStampAsync(TestAgent agent) =>
        (await agent.Http.GetFromJsonAsync<AgentSettings>("/api/agent/settings", TestUser.Json))!.AccessPolicyStamp;

    [Fact]
    public async Task An_agent_receives_every_policy_of_its_workspace_live_first_then_tombstones_under_the_stamp_its_settings_carry()
    {
        var admin = await api.NewClient().SignedUpAsync("apd-set");
        var agent = await admin.ConnectAgentAsync();
        var tombstone = await SeedAsync(admin, version: 2, cleared: true);
        var live = await SeedAsync(admin, version: 3);
        var other = await api.NewClient().SignedUpAsync("apd-other");
        await SeedAsync(other);

        var set = await FetchAsync(agent);

        Assert.Equal([live, tombstone], set.Policies.Select(p => p.ProjectId));
        Assert.All(set.Policies, p => Assert.Equal(admin.WorkspaceId, p.WorkspaceId));
        Assert.Equal(AccessPolicyDelivery.Stamp([(tombstone, 2), (live, 3)], enforced: false), set.Stamp);
        Assert.Equal(set.Stamp, await SettingsStampAsync(agent));
        Assert.Equal(AccessPolicySigning.Hash(set.Policies[0].Document), set.Policies[0].Hash);
    }

    [Fact]
    public async Task The_stamp_is_absent_with_no_policy_and_the_switch_off_and_moves_with_a_new_version_and_with_the_switch()
    {
        var admin = await api.NewClient().SignedUpAsync("apd-stamp");
        var agent = await admin.ConnectAgentAsync();
        Assert.Null(await SettingsStampAsync(agent));
        Assert.Equal("", (await FetchAsync(agent)).Stamp);

        var project = await SeedAsync(admin);
        var first = await SettingsStampAsync(agent);
        await using (var db = api.Db())
        {
            await db.ProjectAccessPolicies.Where(p => p.ProjectId == project).ExecuteUpdateAsync(s => s.SetProperty(p => p.Version, 2));
        }

        var second = await SettingsStampAsync(agent);
        (await admin.SendAsync(HttpMethod.Put, $"/api/workspaces/{admin.WorkspaceId}/settings", new { enforceAccessPolicies = true })).EnsureSuccessStatusCode();
        var third = await SettingsStampAsync(agent);

        Assert.NotNull(first);
        Assert.Equal(3, new[] { first, second, third }.Distinct().Count());
        Assert.Equal(AccessPolicyDelivery.Stamp([(project, 2)], enforced: true), third);
    }

    [Fact]
    public async Task At_most_two_hundred_policies_are_delivered_live_ones_first()
    {
        var admin = await api.NewClient().SignedUpAsync("apd-cap");
        var agent = await admin.ConnectAgentAsync();
        var tombstone = await SeedAsync(admin, cleared: true);
        for (var i = 0; i < AccessPolicyDelivery.PoliciesMax; i++) await SeedAsync(admin);

        var set = await FetchAsync(agent);

        Assert.Equal(AccessPolicyDelivery.PoliciesMax, set.Policies.Count);
        Assert.DoesNotContain(set.Policies, p => p.ProjectId == tombstone);
        Assert.All(set.Policies, p => Assert.False(p.Document.Cleared));
    }

    [Fact]
    public async Task A_viewers_agent_receives_the_set_and_the_agent_of_a_user_no_longer_a_member_receives_nothing()
    {
        var admin = await api.NewClient().SignedUpAsync("apd-member");
        var viewer = await RemoteKit.MemberAsync(api, admin, "apd-viewer");
        var leaver = await RemoteKit.MemberAsync(api, admin, "apd-leaver");
        await SeedAsync(admin);
        var viewerAgent = await viewer.ConnectAgentAsync(admin.WorkspaceId);
        var leaverAgent = await leaver.ConnectAgentAsync(admin.WorkspaceId);
        Assert.Single((await FetchAsync(leaverAgent)).Policies);

        await using (var db = api.Db())
        {
            // A viewer cannot connect a machine, so the member who did becomes one; the agent still reads the set.
            await db.WorkspaceMembers.Where(m => m.WorkspaceId == admin.WorkspaceId && m.UserId == viewer.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, Roles.Viewer));

            // The membership ended and the agent was not revoked (the removal endpoint also revokes it; this is the check behind that).
            var now = api.Clock.GetUtcNow();
            await db.WorkspaceMembers.Where(m => m.WorkspaceId == admin.WorkspaceId && m.UserId == leaver.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.RemovedAt, now));
        }

        Assert.Single((await FetchAsync(viewerAgent)).Policies);
        // Refused before the endpoint's own member check (which would answer 404): the agent's sign-in already requires the membership.
        Assert.Equal(HttpStatusCode.Unauthorized, (await leaverAgent.Http.GetAsync(Route)).StatusCode);
    }
}
