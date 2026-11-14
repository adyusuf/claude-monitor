using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>The answer that asks to be remembered (ADR-0019): what it makes, what refuses it, and the re-authentication an always-rule needs.</summary>
[Collection(ApiGroup.Name)]
public sealed class ApprovalRuleCreationTests(ApiFactory api)
{
    private static readonly object Make = new { command = "make build" };

    [Fact]
    public async Task A_remembered_allow_makes_one_rule_for_the_exact_command_in_the_requests_project_and_audits_it()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);

        var requestId = await ApprovalRuleKit.RememberAsync(w, "Bash", Make, ApprovalDurations.FiveMinutes);

        var request = await ApprovalRuleKit.RequestRowAsync(api, requestId);
        var rule = Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId));
        Assert.Equal(("Bash", "make build", "5m"), (rule.ToolName, rule.Command, rule.Duration));
        Assert.Equal((w.Owner.Id, w.AgentId, await ApprovalRuleKit.RepoIdAsync(api, w.WorkspaceId)), (rule.UserId, rule.AgentId, rule.RepoId));
        Assert.Equal((request.AnsweredAt, request.AnsweredAt + TimeSpan.FromMinutes(5), requestId), (rule.CreatedAt, rule.ExpiresAt, rule.CreatedFromRequest));
        Assert.Equal((0, null, null), (rule.UseCount, rule.LastUsedAt, rule.RevokedAt));
        var audit = Assert.Single(await ApprovalRuleKit.AuditAsync(api, w.WorkspaceId, AuditActions.ApprovalRuleCreated));
        Assert.Equal((w.Owner.Id, "approval_rule", rule.Id.ToString()), (audit.ActorUserId, audit.TargetType, audit.TargetId));
        Assert.Equal(("Bash", "5m", requestId), (audit.Detail!.RootElement.GetProperty("ToolName").GetString(), audit.Detail.RootElement.GetProperty("Duration").GetString(),
            audit.Detail.RootElement.GetProperty("request").GetGuid()));
    }

    [Theory]
    [InlineData(ApprovalDurations.FiveMinutes, 5)]
    [InlineData(ApprovalDurations.TenMinutes, 10)]
    [InlineData(ApprovalDurations.ThirtyMinutes, 30)]
    [InlineData(ApprovalDurations.OneHour, 60)]
    public async Task A_timed_rule_ends_after_the_asked_length(string remember, int minutes)
    {
        var w = await ApprovalRuleKit.WorldAsync(api);

        await ApprovalRuleKit.RememberAsync(w, "Bash", Make, remember);

        var rule = Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId));
        Assert.Equal((remember, rule.CreatedAt + TimeSpan.FromMinutes(minutes)), (rule.Duration, rule.ExpiresAt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ApprovalDurations.Once)]
    public async Task An_allow_answered_once_makes_no_rule_and_still_answers_the_request(string? remember)
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", Make);

        var response = await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, remember: remember);

        var row = await ApprovalRuleKit.RequestRowAsync(api, request.Id);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId));
        Assert.Equal((PermissionStatuses.Answered, PermissionDecisions.Allow), (row.Status, row.Decision));
    }

    [Fact]
    public async Task A_duration_with_a_deny_or_an_unknown_duration_is_a_validation_error_and_the_request_stays_open()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", Make);

        await RemoteKit.AssertInvalidAsync(await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, PermissionDecisions.Deny, ApprovalDurations.Always), "remember", CommandEndpoints.RuleNotAllowed);
        await RemoteKit.AssertInvalidAsync(await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, remember: "forever"), "remember", "invalid_duration");

        Assert.Equal(PermissionStatuses.Open, (await ApprovalRuleKit.RequestRowAsync(api, request.Id)).Status);
        Assert.Empty(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId));
    }

    [Theory]
    [InlineData("WebFetch", """{"url":"https://example.invalid/"}""")]
    [InlineData("mcp__server__tool", """{"x":"y"}""")]
    [InlineData("Bash", """{"command":"make build","run_in_background":true}""")]
    [InlineData("Bash", """{"command":"echo [masked:token]"}""")]
    [InlineData("Write", """{"file_path":"/repo/.git/hooks/pre-commit"}""")]
    public async Task A_request_that_cannot_have_a_rule_refuses_the_remember_with_a_conflict_and_can_still_be_answered_once(string tool, string json)
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, tool, JsonSerializer.Deserialize<JsonElement>(json), accessScope: AccessScopes.Inside);

        await RemoteKit.AssertProblemAsync(await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, remember: ApprovalDurations.OneHour), HttpStatusCode.Conflict, CommandEndpoints.RuleNotAllowed);

        Assert.Equal(PermissionStatuses.Open, (await ApprovalRuleKit.RequestRowAsync(api, request.Id)).Status);
        Assert.Empty(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId));
        Assert.Equal(HttpStatusCode.NoContent, (await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id)).StatusCode);
    }

    [Theory]
    [InlineData("production", "application")]
    [InlineData("test", "database")]
    public async Task A_strict_machine_by_its_set_role_never_remembers_an_allow(string environment, string function)
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        (await w.Owner.SendAsync(HttpMethod.Put, $"/api/agents/{w.AgentId}/role", new { environment, function })).EnsureSuccessStatusCode();
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", Make);

        await RemoteKit.AssertProblemAsync(await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, remember: ApprovalDurations.FiveMinutes), HttpStatusCode.Conflict, CommandEndpoints.RuleNotAllowed);

        Assert.Empty(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId));
    }

    [Fact]
    public async Task A_machine_that_claims_to_be_production_never_remembers_an_allow_even_with_no_role_set()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        (await w.Agent.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(ExecLevels.Off, false, null, null, 2, ClaimedEnvironment: MachineEnvironments.Production),
            TestUser.Json)).EnsureSuccessStatusCode();
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", Make);

        await RemoteKit.AssertProblemAsync(await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, remember: ApprovalDurations.FiveMinutes), HttpStatusCode.Conflict, CommandEndpoints.RuleNotAllowed);

        Assert.Empty(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId));
    }

    [Fact]
    public async Task A_person_outside_the_workspace_cannot_answer_a_request_and_so_cannot_remember_it()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var stranger = await api.NewClient().SignedUpAsync("ar-stranger");
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", Make);

        var response = await ApprovalRuleKit.AnswerAsync(stranger, request.Id, remember: ApprovalDurations.OneHour);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId));
    }
}
