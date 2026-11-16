using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.AccessPolicies;
using Podium.Api.Approvals;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Remote;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Podium.Api.Tests;

/// <summary>
/// A stored access report the API cannot read counts as none, and (F-302) the four paths that read it log that once as a warning with the agent's id
/// and the kind of error only: the stored text came from a machine and is never logged. Old call sites without a logger behave as before.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class AccessReportsLogTests(ApiFactory api)
{
    private const string Secret = "SECRET-marker-never-logged";
    private const string RememberCategory = "Podium.Api.Approvals.ApprovalRuleCreation"; // internal: the category is its full name
    private const string ReadPath = "/work/src/a.txt";
    private static readonly string Unreadable = JsonSerializer.Serialize(new { ceiling = Secret });

    private static void AssertOneUnreadableWarning(TracingLog log, string category, Guid agentId)
    {
        var entry = Assert.Single(log.Entries, e => e.Category == category && e.Level == LogLevel.Warning);
        Assert.Contains("cannot be read", entry.Text, StringComparison.Ordinal);
        Assert.Contains(agentId.ToString(), entry.Everything, StringComparison.Ordinal);
    }

    private static void AssertNothingSensitive(TracingLog log, params string[] more)
    {
        Assert.NotEmpty(log.Entries);
        foreach (var entry in log.Entries)
        {
            Assert.DoesNotContain(Secret, entry.Everything, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, entry.Text, StringComparison.Ordinal);
            foreach (var other in more) Assert.DoesNotContain(other, entry.Everything, StringComparison.Ordinal);
        }
    }

    // ---- the reader itself ------------------------------------------------------------------------------------------------

    [Fact]
    public void An_unreadable_stored_report_is_logged_once_as_a_warning_with_the_agent_id_and_the_kind_of_error_and_still_counts_as_none()
    {
        var log = new TracingLog();
        var agentId = Guid.NewGuid();

        var report = AccessReports.Read(Unreadable, log.CreateLogger("test"), agentId);

        Assert.Null(report);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(agentId.ToString(), entry.Everything, StringComparison.Ordinal);
        Assert.Contains(nameof(JsonException), entry.Everything, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_from_the_stored_text_or_the_error_message_reaches_the_log()
    {
        var log = new TracingLog();

        AccessReports.Read(Unreadable, log.CreateLogger("test"), Guid.NewGuid());

        AssertNothingSensitive(log);
        Assert.DoesNotContain("$.ceiling", log.Entries.Single().Everything, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_or_readable_report_logs_nothing_and_a_call_without_a_logger_still_answers_none()
    {
        var log = new TracingLog();
        var readable = AccessReports.Store(new AccessReport(new AccessCeilingReport(["/work"], [], [], false), []));

        Assert.Null(AccessReports.Read(null, log.CreateLogger("test"), Guid.NewGuid()));
        Assert.NotNull(AccessReports.Read(readable, log.CreateLogger("test"), Guid.NewGuid()));
        Assert.Null(AccessReports.Read(Unreadable));

        Assert.Empty(log.Entries);
    }

    // ---- the four paths ---------------------------------------------------------------------------------------------------

    /// <summary>One person with one machine and a session, a live policy that covers the session's project, and a stored report on the machine that cannot be read.</summary>
    private async Task<RuleWorld> WorldAsync(string tag)
    {
        var w = await ApprovalRuleKit.WorldAsync(api, tag);
        var projectId = (await ProjectKit.CreateProjectAsync(w.Owner, "Policy project")).GetProperty("id").GetGuid();
        var now = api.Clock.GetUtcNow();
        await using var db = api.Db();
        var row = new ProjectAccessPolicy
        {
            WorkspaceId = w.WorkspaceId,
            ProjectId = projectId,
            Version = 1,
            Document = AccessPolicyStore.Write(new AccessPolicyDocument(AccessPolicyRules.Format, [ApprovalRuleKit.Project], false, new([], [], []), new([], [], []), new([], [], []), false, false)),
            DocumentHash = new string('0', AccessPolicyRules.Sha256HexLength),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ProjectAccessPolicies.Add(row);
        db.ProjectAccessPolicyKeys.Add(new ProjectAccessPolicyKey { PolicyId = row.Id, ProjectKey = ApprovalRuleKit.Project, WorkspaceId = w.WorkspaceId });
        (await db.Agents.SingleAsync(a => a.Id == w.AgentId)).AccessReport = Unreadable;
        await db.SaveChangesAsync();
        return w;
    }

    private WebApplicationFactory<Program> Host(TracingLog log) => api.WithWebHostBuilder(b => b.ConfigureLogging(l =>
    {
        l.AddProvider(log);
        l.AddFilter<TracingLog>(null, LogLevel.Trace);
    }));

    private static HttpClient AgentOn(WebApplicationFactory<Program> host, TestAgent agent)
    {
        var http = host.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", agent.Tokens.AccessToken);
        http.DefaultRequestHeaders.Add(AgentHeaders.Version, "0.3.0");
        return http;
    }

    [Fact]
    public async Task The_machine_page_logs_the_unreadable_report_and_still_answers_with_no_report()
    {
        var w = await WorldAsync("arl-web");
        var log = new TracingLog();
        await using var host = Host(log);
        var owner = await HostKit.SignInAsync(api, host, w.Owner);

        var detail = await owner.GetJsonAsync($"/api/agents/{w.AgentId}/detail");

        Assert.Equal(JsonValueKind.Null, detail.GetProperty("access").GetProperty("ceiling").ValueKind);
        AssertOneUnreadableWarning(log, typeof(MachineDetails).FullName!, w.AgentId);
        AssertNothingSensitive(log, w.Agent.Tokens.AccessToken);
    }

    [Fact]
    public async Task The_agents_machine_tool_logs_the_unreadable_report_too()
    {
        var w = await WorldAsync("arl-tool");
        var log = new TracingLog();
        await using var host = Host(log);
        using var agent = AgentOn(host, w.Agent);

        var response = await agent.GetAsync($"/api/agent/machines/{w.AgentId}/detail");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertOneUnreadableWarning(log, typeof(MachineDetails).FullName!, w.AgentId);
        AssertNothingSensitive(log, w.Agent.Tokens.AccessToken);
    }

    [Fact]
    public async Task The_approvals_list_logs_the_unreadable_report_for_a_request_it_checks_and_offers_no_rule()
    {
        var w = await WorldAsync("arl-list");
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Read", new { file_path = ReadPath }, accessScope: AccessScopes.Inside);
        var log = new TracingLog();
        await using var host = Host(log);
        var owner = await HostKit.SignInAsync(api, host, w.Owner);

        var rows = await owner.GetJsonAsync($"/api/workspaces/{w.WorkspaceId}/permission-requests");

        var row = rows.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == request.Id);
        Assert.False(row.GetProperty("canRemember").GetBoolean());
        AssertOneUnreadableWarning(log, typeof(ApprovalRuleAccess).FullName!, w.AgentId);
        AssertNothingSensitive(log, w.Agent.Tokens.AccessToken);
    }

    [Fact]
    public async Task Remembering_an_allow_logs_the_unreadable_report_and_is_refused_as_outside_the_access()
    {
        var w = await WorldAsync("arl-remember");
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Read", new { file_path = ReadPath }, accessScope: AccessScopes.Inside);
        var log = new TracingLog();
        await using var host = Host(log);
        var owner = await HostKit.SignInAsync(api, host, w.Owner);

        var response = await owner.PostAsync($"/api/permission-requests/{request.Id}/answer",
            new { decision = PermissionDecisions.Allow, remember = ApprovalDurations.OneHour });

        await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Conflict, CommandEndpoints.RuleOutsideAccess);
        AssertOneUnreadableWarning(log, RememberCategory, w.AgentId);
        AssertNothingSensitive(log, w.Agent.Tokens.AccessToken);
    }

    [Fact]
    public async Task A_new_request_that_asks_for_an_inline_answer_logs_the_unreadable_report_while_matching_and_waits_for_the_owner()
    {
        var w = await WorldAsync("arl-match");
        var log = new TracingLog();
        await using var host = Host(log);
        using var agent = AgentOn(host, w.Agent);

        var response = await agent.PostAsJsonAsync("/api/agent/permission-requests",
            new PermissionRequestCreate(HarnessKinds.ClaudeCode, w.Session, "Read", JsonSerializer.SerializeToElement(new { file_path = ReadPath }), 120, true, AccessScopes.Inside),
            TestUser.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.Content.ReadFromJsonAsync<PermissionRequestCreated>(TestUser.Json))!.Answer);
        AssertOneUnreadableWarning(log, typeof(ApprovalRules).FullName!, w.AgentId);
        AssertNothingSensitive(log, w.Agent.Tokens.AccessToken);
    }
}
