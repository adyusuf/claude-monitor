using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Approvals;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// Standing approvals need a member or above (F-163): a viewer cannot make one and no rule of theirs matches; a member demoted below member loses
/// their live rules in that workspace, each revoke audited. A member or above keeps everything; a viewer still answers once.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class ApprovalRuleFloorTests(ApiFactory api)
{
    private static readonly object Make = new { command = "make build" };

    private sealed record Team(TestUser Admin, TestUser Member, TestAgent Agent, string Session)
    {
        public Guid WorkspaceId => Admin.WorkspaceId;
        public RuleWorld World => new(Member, Agent, Session);
    }

    private async Task<Team> TeamAsync(string tag)
    {
        var admin = await api.NewClient().SignedUpAsync(tag + "-admin");
        var member = await RemoteKit.MemberAsync(api, admin, tag + "-member");
        var agent = await member.ConnectAgentAsync(admin.WorkspaceId);
        return new Team(admin, member, agent, await ApprovalRuleKit.StartSessionAsync(api, agent));
    }

    private static async Task SetRoleAsync(Team t, string role) =>
        (await t.Admin.SendAsync(HttpMethod.Patch, $"/api/workspaces/{t.WorkspaceId}/members/{t.Member.Id}", new { role })).EnsureSuccessStatusCode();

    private static async Task<PermissionAnswerMessage?> InlineAsync(Team t) =>
        (await ApprovalRuleKit.AskAsync(t.Agent, t.Session, "Bash", Make, inline: true)).Answer;

    [Fact]
    public async Task A_member_promoted_keeps_their_rules_and_they_still_answer_at_once()
    {
        var t = await TeamAsync("fl-keep");
        await ApprovalRuleKit.RememberAsync(t.World, "Bash", Make, ApprovalDurations.OneHour);

        await SetRoleAsync(t, "admin");

        var rule = Assert.Single(await ApprovalRuleKit.RulesAsync(api, t.WorkspaceId));
        Assert.Null(rule.RevokedAt);
        Assert.NotNull(await InlineAsync(t));
        Assert.Empty(await ApprovalRuleKit.AuditAsync(api, t.WorkspaceId, AuditActions.ApprovalRuleRevoked));
    }

    [Fact]
    public async Task A_member_demoted_to_viewer_loses_their_live_rules_each_revoke_audited()
    {
        var t = await TeamAsync("fl-demote");
        await ApprovalRuleKit.RememberAsync(t.World, "Bash", Make, ApprovalDurations.OneHour);
        await ApprovalRuleKit.RememberAsync(t.World, "Bash", new { command = "make test" }, ApprovalDurations.Always);
        Assert.NotNull(await InlineAsync(t));

        await SetRoleAsync(t, "viewer");

        var rules = await ApprovalRuleKit.RulesAsync(api, t.WorkspaceId);
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.Equal(api.Clock.GetUtcNow(), r.RevokedAt));
        var audit = await ApprovalRuleKit.AuditAsync(api, t.WorkspaceId, AuditActions.ApprovalRuleRevoked);
        Assert.Equal(rules.Select(r => r.Id).Order(), audit.Select(a => Guid.Parse(a.TargetId!)).Order());
        Assert.All(audit, a => Assert.Equal(t.Admin.Id, a.ActorUserId));
        Assert.All(audit, a => Assert.Contains(ApprovalRuleFloor.RoleChanged, a.Detail!.RootElement.GetRawText(), StringComparison.Ordinal));
        Assert.Null(await InlineAsync(t));
    }

    [Fact]
    public async Task A_viewer_answers_once_but_cannot_make_a_rule()
    {
        var t = await TeamAsync("fl-viewer");
        await SetRoleAsync(t, "viewer");
        var first = await ApprovalRuleKit.AskAsync(t.Agent, t.Session, "Bash", Make);
        var second = await ApprovalRuleKit.AskAsync(t.Agent, t.Session, "Bash", Make);

        var remembered = await ApprovalRuleKit.AnswerAsync(t.Member, first.Id, remember: ApprovalDurations.OneHour);
        var once = await ApprovalRuleKit.AnswerAsync(t.Member, second.Id);

        Assert.Equal(HttpStatusCode.Conflict, remembered.StatusCode);
        Assert.Equal("rule_not_allowed", (await remembered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.NoContent, once.StatusCode);
        Assert.Empty(await ApprovalRuleKit.RulesAsync(api, t.WorkspaceId));
    }

    [Fact]
    public async Task A_viewers_rule_left_from_before_matches_nothing()
    {
        var t = await TeamAsync("fl-left");
        await SetRoleAsync(t, "viewer");
        // A rule a viewer still holds (made before the floor, or written by anything else): it must not allow anything.
        var rule = ApprovalRuleKit.Build(api, t.World, await ApprovalRuleKit.RepoIdAsync(api, t.WorkspaceId), "make build");
        rule.WorkspaceId = t.WorkspaceId;
        await using (var db = api.Db())
        {
            db.ApprovalRules.Add(rule);
            await db.SaveChangesAsync();
        }

        Assert.Null(await InlineAsync(t));
        await using var check = api.Db();
        Assert.Equal(0, await check.ApprovalRules.Where(r => r.Id == rule.Id).Select(r => r.UseCount).SingleAsync());
    }
}
