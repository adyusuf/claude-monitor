using System.Net;
using System.Text.Json;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>The approvals page's reads and the revoke (ADR-0019 §11-13): the caller's own requests and rules, and what the page is told about remembering.</summary>
[Collection(ApiGroup.Name)]
public sealed class ApprovalEndpointsTests(ApiFactory api)
{
    private static readonly object Make = new { command = "make build" };

    private static string Id(JsonElement row) => row.GetProperty("id").GetString()!;

    // ---- the requests ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_open_requests_of_the_callers_sessions_are_listed_oldest_first_with_their_machine_and_project()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var first = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", Make);
        api.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", new { command = "make test" });

        var list = await ApprovalRuleKit.ListRequestsAsync(w.Owner, w.WorkspaceId);

        Assert.Equal(new[] { first.Id.ToString(), second.Id.ToString() }, list.EnumerateArray().Select(Id));
        var row = list[0];
        Assert.Equal(("laptop-1", "Repo Name", "Bash", "open"), (row.GetProperty("hostname").GetString(), row.GetProperty("projectName").GetString(),
            row.GetProperty("toolName").GetString(), row.GetProperty("status").GetString()));
        Assert.Equal(("make build", w.AgentId), (row.GetProperty("toolInput").GetProperty("command").GetString(), row.GetProperty("agentId").GetGuid()));
    }

    [Fact]
    public async Task Another_member_of_the_workspace_sees_none_of_the_requests_and_a_stranger_gets_not_found()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", Make);
        var member = await RemoteKit.MemberAsync(api, w.Owner, "ar-member");
        var stranger = await api.NewClient().SignedUpAsync("ar-stranger");

        var seenByMember = await ApprovalRuleKit.ListRequestsAsync(member, w.WorkspaceId);
        var seenByStranger = await stranger.Http.GetAsync($"/api/workspaces/{w.WorkspaceId}/permission-requests");

        Assert.Equal(0, seenByMember.GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, seenByStranger.StatusCode);
    }

    [Fact]
    public async Task A_request_that_ran_out_of_time_is_not_open_even_before_the_housekeeper_marks_it()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", Make, waitSeconds: 5);
        var live = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", new { command = "make test" }, waitSeconds: 120);
        api.Clock.Advance(TimeSpan.FromSeconds(6));

        var list = await ApprovalRuleKit.ListRequestsAsync(w.Owner, w.WorkspaceId);

        Assert.Equal(new[] { live.Id.ToString() }, list.EnumerateArray().Select(Id));
    }

    [Fact]
    public async Task The_answered_list_names_who_answered_and_which_rule_when_a_rule_did_and_is_newest_first()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var byPerson = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", new { command = "make test" });
        Assert.Equal(HttpStatusCode.NoContent, (await ApprovalRuleKit.AnswerAsync(w.Owner, byPerson.Id, PermissionDecisions.Deny)).StatusCode);
        await ApprovalRuleKit.RememberAsync(w, "Bash", Make, ApprovalDurations.OneHour);
        var rule = Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId));
        api.Clock.Advance(TimeSpan.FromSeconds(1));
        var byRule = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", Make, inline: true);

        var list = await ApprovalRuleKit.ListRequestsAsync(w.Owner, w.WorkspaceId, "answered");

        Assert.Equal(byRule.Id.ToString(), Id(list[0]));
        Assert.Equal((PermissionDecisions.Allow, rule.Id, "Test Person"), (list[0].GetProperty("decision").GetString(), list[0].GetProperty("answeredByRule").GetGuid(),
            list[0].GetProperty("answeredByName").GetString()));
        var person = list.EnumerateArray().Single(r => Id(r) == byPerson.Id.ToString());
        Assert.Equal((PermissionDecisions.Deny, JsonValueKind.Null, "Test Person"), (person.GetProperty("decision").GetString(), person.GetProperty("answeredByRule").ValueKind,
            person.GetProperty("answeredByName").GetString()));
    }

    [Theory]
    [InlineData("Bash", """{"command":"make build"}""", null, true)]
    [InlineData("Bash", """{"command":"make build","run_in_background":true}""", null, false)]
    [InlineData("WebFetch", """{"url":"https://example.invalid/"}""", null, false)]
    [InlineData("mcp__server__tool", """{"x":"y"}""", null, false)]
    [InlineData("Read", """{"file_path":"/repo/a.txt"}""", null, false)]
    [InlineData("Read", """{"file_path":"/repo/a.txt"}""", "outside", false)]
    [InlineData("Read", """{"file_path":"/repo/a.txt"}""", "inside", true)]
    [InlineData("Read", """{"file_path":"/repo/../etc/passwd"}""", "inside", false)]
    [InlineData("Write", """{"file_path":"/repo/.git/config"}""", "inside", false)]
    public async Task The_page_is_told_a_request_can_be_remembered_exactly_when_the_answer_would_accept_it(string tool, string json, string? scope, bool canRemember)
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, tool, JsonSerializer.Deserialize<JsonElement>(json), accessScope: scope);

        var told = (await ApprovalRuleKit.ListRequestsAsync(w.Owner, w.WorkspaceId))[0].GetProperty("canRemember").GetBoolean();
        var answer = await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, remember: ApprovalDurations.FiveMinutes);

        Assert.Equal(canRemember, told);
        Assert.Equal(canRemember ? HttpStatusCode.NoContent : HttpStatusCode.Conflict, answer.StatusCode);
    }

    [Fact]
    public async Task A_strict_machines_requests_are_flagged_strict_and_can_not_be_remembered()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        (await w.Owner.SendAsync(HttpMethod.Put, $"/api/agents/{w.AgentId}/role", new { environment = "production", function = "application" })).EnsureSuccessStatusCode();
        await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", Make);

        var row = (await ApprovalRuleKit.ListRequestsAsync(w.Owner, w.WorkspaceId))[0];

        Assert.Equal((true, false), (row.GetProperty("strict").GetBoolean(), row.GetProperty("canRemember").GetBoolean()));
    }

    // ---- the rules ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_callers_live_rules_are_listed_newest_first_with_their_machine_project_and_use()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var now = api.Clock.GetUtcNow();
        var older = await ApprovalRuleKit.SeedAsync(api, w, "make build", ApprovalDurations.Always, createdAt: now - TimeSpan.FromMinutes(5), lastUsedAt: now - TimeSpan.FromMinutes(1));
        var newer = await ApprovalRuleKit.SeedAsync(api, w, "make test", ApprovalDurations.OneHour, createdAt: now - TimeSpan.FromMinutes(1), expiresAt: now + TimeSpan.FromMinutes(59));

        var list = await ApprovalRuleKit.ListRulesAsync(w.Owner, w.WorkspaceId);

        Assert.Equal(new[] { newer.Id.ToString(), older.Id.ToString() }, list.EnumerateArray().Select(Id));
        var row = list[1];
        Assert.Equal(("laptop-1", "Repo Name", "Bash", "make build", "always"), (row.GetProperty("hostname").GetString(), row.GetProperty("projectName").GetString(),
            row.GetProperty("toolName").GetString(), row.GetProperty("command").GetString(), row.GetProperty("duration").GetString()));
        Assert.Equal((JsonValueKind.Null, 0, now - TimeSpan.FromMinutes(1)), (row.GetProperty("expiresAt").ValueKind, row.GetProperty("useCount").GetInt32(),
            row.GetProperty("lastUsedAt").GetDateTimeOffset()));
        Assert.Equal(w.AgentId, row.GetProperty("agentId").GetGuid());
    }

    [Fact]
    public async Task A_revoked_or_ended_rule_is_not_listed_and_nobody_else_sees_the_callers_rules()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var now = api.Clock.GetUtcNow();
        var live = await ApprovalRuleKit.SeedAsync(api, w, "make build");
        await ApprovalRuleKit.SeedAsync(api, w, "make test", revokedAt: now - TimeSpan.FromSeconds(1));
        await ApprovalRuleKit.SeedAsync(api, w, "make lint", ApprovalDurations.FiveMinutes, createdAt: now - TimeSpan.FromMinutes(6), expiresAt: now);
        var member = await RemoteKit.MemberAsync(api, w.Owner, "ar-member");

        var mine = await ApprovalRuleKit.ListRulesAsync(w.Owner, w.WorkspaceId);
        var theirs = await ApprovalRuleKit.ListRulesAsync(member, w.WorkspaceId);

        Assert.Equal(new[] { live.Id.ToString() }, mine.EnumerateArray().Select(Id));
        Assert.Equal(0, theirs.GetArrayLength());
    }

    [Fact]
    public async Task A_stranger_to_the_workspace_gets_not_found_for_the_rule_list()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var stranger = await api.NewClient().SignedUpAsync("ar-stranger");

        var response = await stranger.Http.GetAsync($"/api/workspaces/{w.WorkspaceId}/approval-rules");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_use_by_the_auto_allow_shows_in_the_rule_list_as_a_count_and_a_last_used_time()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        await ApprovalRuleKit.RememberAsync(w, "Bash", Make, ApprovalDurations.OneHour);
        api.Clock.Advance(TimeSpan.FromSeconds(10));
        await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", Make, inline: true);

        var row = (await ApprovalRuleKit.ListRulesAsync(w.Owner, w.WorkspaceId))[0];

        Assert.Equal((1, api.Clock.GetUtcNow()), (row.GetProperty("useCount").GetInt32(), row.GetProperty("lastUsedAt").GetDateTimeOffset()));
    }

    // ---- revoking -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Revoking_a_rule_removes_it_from_the_list_stamps_it_and_writes_one_audit_row()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var rule = await ApprovalRuleKit.SeedAsync(api, w, "make build", ApprovalDurations.Always);

        var response = await w.Owner.SendAsync(HttpMethod.Delete, $"/api/approval-rules/{rule.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, (await ApprovalRuleKit.ListRulesAsync(w.Owner, w.WorkspaceId)).GetArrayLength());
        Assert.Equal(api.Clock.GetUtcNow(), Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId)).RevokedAt);
        var audit = Assert.Single(await ApprovalRuleKit.AuditAsync(api, w.WorkspaceId, AuditActions.ApprovalRuleRevoked));
        Assert.Equal((w.Owner.Id, "approval_rule", rule.Id.ToString()), (audit.ActorUserId, audit.TargetType, audit.TargetId));
        Assert.Equal(("Bash", "always"), (audit.Detail!.RootElement.GetProperty("ToolName").GetString(), audit.Detail.RootElement.GetProperty("Duration").GetString()));
    }

    [Fact]
    public async Task Revoking_the_same_rule_again_or_one_that_does_not_exist_is_not_found_and_changes_nothing()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var rule = await ApprovalRuleKit.SeedAsync(api, w, "make build");
        (await w.Owner.SendAsync(HttpMethod.Delete, $"/api/approval-rules/{rule.Id}")).EnsureSuccessStatusCode();
        var stamped = Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId)).RevokedAt;
        api.Clock.Advance(TimeSpan.FromSeconds(30));

        var again = await w.Owner.SendAsync(HttpMethod.Delete, $"/api/approval-rules/{rule.Id}");
        var unknown = await w.Owner.SendAsync(HttpMethod.Delete, $"/api/approval-rules/{Guid.CreateVersion7()}");

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (again.StatusCode, unknown.StatusCode));
        Assert.Equal(stamped, Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId)).RevokedAt);
        Assert.Single(await ApprovalRuleKit.AuditAsync(api, w.WorkspaceId, AuditActions.ApprovalRuleRevoked));
    }

    [Fact]
    public async Task Only_the_owner_of_a_rule_can_revoke_it_another_member_gets_not_found_and_the_rule_stays_live()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var rule = await ApprovalRuleKit.SeedAsync(api, w, "make build");
        var member = await RemoteKit.MemberAsync(api, w.Owner, "ar-member");

        var response = await member.SendAsync(HttpMethod.Delete, $"/api/approval-rules/{rule.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId)).RevokedAt);
        Assert.Empty(await ApprovalRuleKit.AuditAsync(api, w.WorkspaceId, AuditActions.ApprovalRuleRevoked));
    }
}
