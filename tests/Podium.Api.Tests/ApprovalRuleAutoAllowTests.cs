using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Approvals;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// A request an approval rule allows (ADR-0019 §7, §8): the agent that reads the answer inline gets an allow in the response of its own request, with
/// no person present, and it is counted and audited; and everything that must not match (another command, project, machine, person, an ended, revoked
/// or strict rule).
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class ApprovalRuleAutoAllowTests(ApiFactory api)
{
    private static readonly object Make = new { command = "make build" };

    private async Task<(RuleWorld World, ApprovalRule Rule)> RememberedAsync(string duration = ApprovalDurations.OneHour)
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        await ApprovalRuleKit.RememberAsync(w, "Bash", Make, duration);
        return (w, Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId)));
    }

    private static Task<PermissionRequestCreated> InlineAsync(RuleWorld w, object? input = null, string? session = null, string tool = "Bash") =>
        ApprovalRuleKit.AskAsync(w.Agent, session ?? w.Session, tool, input ?? Make, inline: true);

    [Fact]
    public async Task A_request_a_rule_covers_is_answered_allow_in_its_own_response_with_no_reason_and_no_person()
    {
        var (w, rule) = await RememberedAsync();

        var created = await InlineAsync(w);

        Assert.NotNull(created.Answer);
        Assert.Equal((created.Id, w.Session, PermissionDecisions.Allow, null), (created.Answer.Id, created.Answer.SessionExternalId, created.Answer.Decision, created.Answer.Reason));
        Assert.Null(created.Answer.Answers);
        var row = await ApprovalRuleKit.RequestRowAsync(api, created.Id);
        Assert.Equal((PermissionStatuses.Answered, PermissionDecisions.Allow, rule.Id), (row.Status, row.Decision, row.AnsweredByRule));
        Assert.Equal((rule.UserId, api.Clock.GetUtcNow(), null), (row.AnsweredBy, row.AnsweredAt, row.Reason));
    }

    [Fact]
    public async Task Each_auto_allow_counts_a_use_stamps_the_time_and_writes_one_audit_row_for_the_rules_owner()
    {
        var (w, rule) = await RememberedAsync();
        var first = await InlineAsync(w);
        api.Clock.Advance(TimeSpan.FromSeconds(30));

        var second = await InlineAsync(w);

        var used = Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId));
        Assert.Equal((2, api.Clock.GetUtcNow()), (used.UseCount, used.LastUsedAt));
        var audit = await ApprovalRuleKit.AuditAsync(api, w.WorkspaceId, AuditActions.PermissionAutoAllowed);
        Assert.Equal(new[] { first.Id.ToString(), second.Id.ToString() }, audit.Select(a => a.TargetId));
        Assert.Equal((rule.UserId, "permission_request"), (audit[1].ActorUserId, audit[1].TargetType));
        Assert.Equal((rule.Id, "Bash"), (audit[1].Detail!.RootElement.GetProperty("rule").GetGuid(), audit[1].Detail!.RootElement.GetProperty("ToolName").GetString()));
    }

    [Fact]
    public async Task An_auto_allowed_request_leaves_the_session_running_and_never_shows_up_as_open_for_the_owner()
    {
        var (w, _) = await RememberedAsync();

        await InlineAsync(w);

        Assert.NotEqual(SessionStatuses.Waiting, await ApprovalRuleKit.SessionStatusAsync(api, w.Agent, w.Session));
        Assert.Equal(0, (await ApprovalRuleKit.ListRequestsAsync(w.Owner, w.WorkspaceId)).GetArrayLength());
    }

    [Fact]
    public async Task An_agent_that_does_not_read_the_answer_inline_gets_no_allow_and_the_rule_is_not_used_up()
    {
        var (w, rule) = await RememberedAsync();

        var created = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", Make, inline: false);

        Assert.Null(created.Answer);
        var row = await ApprovalRuleKit.RequestRowAsync(api, created.Id);
        Assert.Equal((PermissionStatuses.Open, null), (row.Status, row.AnsweredByRule));
        Assert.Equal(SessionStatuses.Waiting, await ApprovalRuleKit.SessionStatusAsync(api, w.Agent, w.Session));
        var untouched = Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId));
        Assert.Equal((rule.Id, 0, null), (untouched.Id, untouched.UseCount, untouched.LastUsedAt));
        Assert.Empty(await ApprovalRuleKit.AuditAsync(api, w.WorkspaceId, AuditActions.PermissionAutoAllowed));
    }

    [Theory]
    [InlineData("make build --force")]
    [InlineData("make build ")]
    [InlineData("MAKE BUILD")]
    [InlineData("make")]
    public async Task A_rule_covers_only_the_exact_command_it_was_made_for(string command)
    {
        var (w, _) = await RememberedAsync();

        var created = await InlineAsync(w, new { command });

        Assert.Null(created.Answer);
        Assert.Equal(PermissionStatuses.Open, (await ApprovalRuleKit.RequestRowAsync(api, created.Id)).Status);
    }

    [Fact]
    public async Task The_same_command_with_a_description_and_a_timeout_is_still_covered_because_they_do_not_change_what_runs()
    {
        var (w, rule) = await RememberedAsync();

        var created = await InlineAsync(w, new { command = "make build", description = "build it", timeout = 60000 });

        Assert.NotNull(created.Answer);
        Assert.Equal(rule.Id, (await ApprovalRuleKit.RequestRowAsync(api, created.Id)).AnsweredByRule);
    }

    [Fact]
    public async Task A_rule_does_not_act_in_another_project()
    {
        var (w, _) = await RememberedAsync();
        var other = await ApprovalRuleKit.StartSessionAsync(api, w.Agent, ApprovalRuleKit.OtherProject);

        var created = await InlineAsync(w, session: other);

        Assert.Null(created.Answer);
    }

    [Fact]
    public async Task A_rule_made_in_a_session_without_a_project_acts_only_in_sessions_without_one()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var loose = await ApprovalRuleKit.StartSessionAsync(api, w.Agent, project: null);
        await ApprovalRuleKit.RememberAsync(w, "Bash", Make, ApprovalDurations.OneHour, session: loose);

        var inLoose = await InlineAsync(w, session: loose);
        var inProject = await InlineAsync(w);

        Assert.NotNull(inLoose.Answer);
        Assert.Null(inProject.Answer);
    }

    [Fact]
    public async Task A_rule_does_not_act_on_another_machine_of_the_same_person()
    {
        var (w, _) = await RememberedAsync();
        var second = await w.Owner.ConnectAgentAsync();
        var session = await ApprovalRuleKit.StartSessionAsync(api, second);

        var created = await ApprovalRuleKit.AskAsync(second, session, "Bash", Make, inline: true);

        Assert.Null(created.Answer);
    }

    [Fact]
    public async Task A_rule_made_by_someone_who_is_not_the_machines_owner_now_does_not_act()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var previous = await api.NewClient().SignedUpAsync("ar-previous");
        await ApprovalRuleKit.SeedAsync(api, w, "make build", userId: previous.Id);

        var created = await InlineAsync(w);

        Assert.Null(created.Answer);
    }

    [Fact]
    public async Task A_rule_that_ended_does_not_act_and_neither_does_one_that_ends_at_this_very_moment()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var now = api.Clock.GetUtcNow();
        await ApprovalRuleKit.SeedAsync(api, w, "make build", ApprovalDurations.FiveMinutes, createdAt: now - TimeSpan.FromMinutes(5), expiresAt: now);
        await ApprovalRuleKit.SeedAsync(api, w, "make test", ApprovalDurations.FiveMinutes, createdAt: now - TimeSpan.FromMinutes(6), expiresAt: now - TimeSpan.FromSeconds(1));

        var atTheEnd = await InlineAsync(w);
        var pastTheEnd = await InlineAsync(w, new { command = "make test" });

        Assert.Null(atTheEnd.Answer);
        Assert.Null(pastTheEnd.Answer);
    }

    [Fact]
    public async Task A_rule_that_still_has_a_moment_to_run_acts()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var now = api.Clock.GetUtcNow();
        await ApprovalRuleKit.SeedAsync(api, w, "make build", ApprovalDurations.FiveMinutes, createdAt: now - TimeSpan.FromMinutes(5), expiresAt: now + TimeSpan.FromTicks(10));

        var created = await InlineAsync(w);

        Assert.NotNull(created.Answer);
    }

    [Fact]
    public async Task A_revoked_rule_does_not_act_from_the_moment_it_is_revoked()
    {
        var (w, rule) = await RememberedAsync();
        Assert.NotNull((await InlineAsync(w)).Answer);
        Assert.Equal(HttpStatusCode.NoContent, (await w.Owner.SendAsync(HttpMethod.Delete, $"/api/approval-rules/{rule.Id}")).StatusCode);

        var after = await InlineAsync(w);

        Assert.Null(after.Answer);
        Assert.Equal(1, Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId)).UseCount);
    }

    [Theory]
    [InlineData("production", "application")]
    [InlineData("test", "database")]
    public async Task A_machine_that_becomes_strict_by_its_set_role_loses_its_rules_and_lowering_the_role_later_brings_none_back(string environment, string function)
    {
        var (w, _) = await RememberedAsync();
        (await w.Owner.SendAsync(HttpMethod.Put, $"/api/agents/{w.AgentId}/role", new { environment, function })).EnsureSuccessStatusCode();

        var whileStrict = await InlineAsync(w);
        (await w.Owner.SendAsync(HttpMethod.Put, $"/api/agents/{w.AgentId}/role", new { environment = "dev", function = "development" })).EnsureSuccessStatusCode();
        var afterLowering = await InlineAsync(w);

        Assert.Null(whileStrict.Answer);
        Assert.Null(afterLowering.Answer);
        Assert.NotNull(Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId)).RevokedAt);
    }

    [Fact]
    public async Task A_machine_that_claims_to_be_production_loses_its_rules_and_dropping_the_claim_later_brings_none_back()
    {
        var (w, _) = await RememberedAsync();
        async Task ReportAsync(string? claim) => (await w.Agent.Http.PutAsJsonAsync("/api/agent/profile",
            new AgentProfile(ExecLevels.Off, false, null, null, 2, ClaimedEnvironment: claim), TestUser.Json)).EnsureSuccessStatusCode();
        await ReportAsync(MachineEnvironments.Production);

        var whileClaiming = await InlineAsync(w);
        await ReportAsync(null);
        var afterDropping = await InlineAsync(w);

        Assert.Null(whileClaiming.Answer);
        Assert.Null(afterDropping.Answer);
        Assert.NotNull(Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId)).RevokedAt);
    }
}
