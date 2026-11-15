using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// A standing approval (ADR-0019) never outlives its machine or its person: removing the member, revoking or moving the agent, enrolling the
/// machine again and deleting the account each end the rules of that agent or person, so they can no longer auto-allow. Rules of others stay.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class ApprovalRuleCascadeTests(ApiFactory api)
{
    private static readonly object Make = new { command = "make build" };

    private async Task<ApprovalRule> RuleAsync(Guid id)
    {
        await using var db = api.Db();
        return await db.ApprovalRules.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private async Task<bool> ExistsAsync(Guid id)
    {
        await using var db = api.Db();
        return await db.ApprovalRules.AnyAsync(r => r.Id == id);
    }

    /// <summary>A member of the owner's workspace with a machine there, a session in the default project and a standing approval for "make build".</summary>
    private async Task<(RuleWorld World, ApprovalRule Rule)> MemberWithRuleAsync(TestUser owner, string tag)
    {
        var member = await RemoteKit.MemberAsync(api, owner, tag);
        var agent = await member.ConnectAgentAsync(owner.WorkspaceId);
        var world = new RuleWorld(owner, agent, await ApprovalRuleKit.StartSessionAsync(api, agent));
        var rule = await ApprovalRuleKit.SeedAsync(api, world, "make build", userId: member.Id);
        return (world, rule);
    }

    [Fact]
    public async Task Removing_a_member_ends_their_rules_and_leaves_the_owners()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "arc-rm");
        var mine = await ApprovalRuleKit.SeedAsync(api, w, "make build");
        var (_, theirs) = await MemberWithRuleAsync(w.Owner, "arc-rm-m");

        Assert.Equal(HttpStatusCode.NoContent, (await w.Owner.SendAsync(HttpMethod.Delete, $"/api/workspaces/{w.WorkspaceId}/members/{theirs.UserId}")).StatusCode);

        Assert.NotNull((await RuleAsync(theirs.Id)).RevokedAt);
        Assert.Null((await RuleAsync(mine.Id)).RevokedAt);
    }

    [Fact]
    public async Task Revoking_an_agent_ends_its_rules_and_another_agents_stay()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "arc-rv");
        var rule = await ApprovalRuleKit.SeedAsync(api, w, "make build");
        var other = await w.Owner.ConnectAgentAsync();
        var kept = await ApprovalRuleKit.SeedAsync(api, w, "make build", agentId: other.Tokens.AgentId);

        Assert.Equal(HttpStatusCode.NoContent, (await w.Owner.PostAsync($"/api/agents/{w.AgentId}/revoke", new { })).StatusCode);

        Assert.NotNull((await RuleAsync(rule.Id)).RevokedAt);
        Assert.Null((await RuleAsync(kept.Id)).RevokedAt);
    }

    [Fact]
    public async Task Moving_an_agent_to_another_workspace_ends_its_rules_so_the_same_request_there_asks_the_owner()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "arc-mv");
        var rule = await ApprovalRuleKit.SeedAsync(api, w, "make build");
        var asked = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", Make, inline: true);
        Assert.NotNull(asked.Answer); // the rule answers while the agent is where it was made
        var created = await w.Owner.PostAsync("/api/workspaces", new { name = "Second" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var target = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await w.Owner.SendAsync(HttpMethod.Patch, $"/api/agents/{w.AgentId}", new { workspaceId = target })).StatusCode);

        Assert.NotNull((await RuleAsync(rule.Id)).RevokedAt);
        var there = await ApprovalRuleKit.StartSessionAsync(api, w.Agent);
        Assert.Null((await ApprovalRuleKit.AskAsync(w.Agent, there, "Bash", Make, inline: true)).Answer);
    }

    [Fact]
    public async Task Enrolling_the_same_machine_again_ends_the_old_agents_rules_and_the_new_agent_is_asked()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "arc-en");
        var rule = await ApprovalRuleKit.SeedAsync(api, w, "make build");

        var again = await w.Owner.ConnectAgentAsync(machineKey: w.Agent.MachineKey);

        Assert.NotEqual(w.AgentId, again.Tokens.AgentId);
        Assert.NotNull((await RuleAsync(rule.Id)).RevokedAt);
        var session = await ApprovalRuleKit.StartSessionAsync(api, again);
        Assert.Null((await ApprovalRuleKit.AskAsync(again, session, "Bash", Make, inline: true)).Answer);
    }

    [Fact]
    public async Task Deleting_the_account_deletes_the_persons_rules_and_leaves_the_owners()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "arc-del");
        var mine = await ApprovalRuleKit.SeedAsync(api, w, "make build");
        var member = await RemoteKit.MemberAsync(api, w.Owner, "arc-del-m");
        var agent = await member.ConnectAgentAsync(w.WorkspaceId);
        await ApprovalRuleKit.StartSessionAsync(api, agent);
        var theirs = await ApprovalRuleKit.SeedAsync(api, w, "make build", userId: member.Id, agentId: agent.Tokens.AgentId);

        Assert.Equal(HttpStatusCode.NoContent, (await member.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE" })).StatusCode);

        Assert.False(await ExistsAsync(theirs.Id));
        Assert.True(await ExistsAsync(mine.Id));
    }
}
