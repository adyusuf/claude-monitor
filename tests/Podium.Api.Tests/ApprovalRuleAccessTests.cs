using System.Net;
using Podium.Api.AccessPolicies;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>The tie between approval rules and access policies for path tools (ADR-0019, ADR-0020 §4): when a rule can be made, and when it can still act.</summary>
[Collection(ApiGroup.Name)]
public sealed class ApprovalRuleAccessTests(ApiFactory api)
{
    private const string ReadPath = "/work/src/a.txt";
    private static readonly AccessOsRules NoRules = new([], [], []);

    private static object ReadOf(string path) => new { file_path = path };

    /// <summary>A live policy for the default project, written as the web would have saved it, and (when asked) reported by the machine as applied.</summary>
    private async Task PolicyAsync(RuleWorld w, AccessOsRules mac, bool includeProjectFolder = false, bool applied = true, bool cleared = false, int version = 1, int reported = 1,
        AccessOsRules? linux = null, AccessOsRules? windows = null)
    {
        var projectId = (await ProjectKit.CreateProjectAsync(w.Owner, "Policy project")).GetProperty("id").GetGuid();
        var document = new AccessPolicyDocument(AccessPolicyRules.Format, [ApprovalRuleKit.Project], includeProjectFolder, linux ?? NoRules, mac, windows ?? NoRules, false, cleared);
        var now = api.Clock.GetUtcNow();
        await using var db = api.Db();
        var row = new ProjectAccessPolicy
        {
            WorkspaceId = w.WorkspaceId,
            ProjectId = projectId,
            Version = version,
            Document = AccessPolicyStore.Write(document),
            DocumentHash = new string('0', AccessPolicyRules.Sha256HexLength),
            Cleared = cleared,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ProjectAccessPolicies.Add(row);
        db.ProjectAccessPolicyKeys.Add(new ProjectAccessPolicyKey { PolicyId = row.Id, ProjectKey = ApprovalRuleKit.Project, WorkspaceId = w.WorkspaceId });
        if (applied)
        {
            var agent = await db.Agents.SingleAsync(a => a.Id == w.AgentId);
            agent.AccessReport = AccessReports.Store(new AccessReport(new AccessCeilingReport([], [], [], true),
                [new AccessAppliedReport(projectId, reported, AccessPolicyStates.Applied, false, null)]));
        }

        await db.SaveChangesAsync();
    }

    private static async Task<HttpResponseMessage> RememberReadAsync(RuleWorld w, string path, string? scope = AccessScopes.Inside)
    {
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Read", ReadOf(path), accessScope: scope);
        return await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, remember: ApprovalDurations.OneHour);
    }

    // ---- no policy: the agent's own "inside" decides ----------------------------------------------------------------------

    [Fact]
    public async Task A_path_inside_the_projects_access_can_be_remembered_as_a_rule_pinned_to_that_exact_path()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);

        var response = await RememberReadAsync(w, ReadPath);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var rule = Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId));
        Assert.Equal(("Read", ReadPath), (rule.ToolName, rule.Command));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AccessScopes.Outside)]
    [InlineData(AccessScopes.Unknown)]
    public async Task A_path_the_agent_did_not_find_inside_is_never_remembered_an_older_agent_that_sends_no_scope_included(string? scope)
    {
        var w = await ApprovalRuleKit.WorldAsync(api);

        var response = await RememberReadAsync(w, ReadPath, scope);

        await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Conflict, CommandEndpoints.RuleOutsideAccess);
        Assert.Empty(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId));
    }

    [Theory]
    [InlineData("/work/src/../secret.txt")]
    [InlineData("/work/./a.txt")]
    [InlineData("/work//a.txt")]
    [InlineData("/work/src/")]
    public async Task A_path_with_a_dot_segment_an_empty_segment_or_a_trailing_separator_is_never_remembered_even_when_the_agent_says_inside(string path)
    {
        var w = await ApprovalRuleKit.WorldAsync(api);

        var response = await RememberReadAsync(w, path);

        await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Conflict, CommandEndpoints.RuleOutsideAccess);
        Assert.Empty(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId));
    }

    [Fact]
    public async Task A_search_in_the_working_folder_without_a_path_and_a_shell_command_do_not_need_the_inside_mark()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);

        var search = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Grep", new { pattern = "todo" });
        var shell = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", new { command = "make build" });

        Assert.Equal(HttpStatusCode.NoContent, (await ApprovalRuleKit.AnswerAsync(w.Owner, search.Id, remember: ApprovalDurations.OneHour)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ApprovalRuleKit.AnswerAsync(w.Owner, shell.Id, remember: ApprovalDurations.OneHour)).StatusCode);
    }

    [Fact]
    public async Task A_search_that_carries_a_path_needs_the_inside_mark_like_a_file_tool()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Grep", new { pattern = "todo", path = "/work" });

        var response = await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, remember: ApprovalDurations.OneHour);

        await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Conflict, CommandEndpoints.RuleOutsideAccess);
    }

    // ---- a live web policy -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/work/src/a.txt", true)]
    [InlineData("/work/secret/key.txt", false)]
    [InlineData("/elsewhere/a.txt", false)]
    public async Task With_an_applied_policy_a_path_is_remembered_only_under_an_allowed_folder_and_under_no_denied_one(string path, bool remembered)
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        await PolicyAsync(w, new AccessOsRules(["/work"], ["/work/secret"], []));

        var response = await RememberReadAsync(w, path);

        Assert.Equal(remembered ? HttpStatusCode.NoContent : HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(remembered ? 1 : 0, (await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId)).Count);
    }

    [Fact]
    public async Task A_policy_the_machine_has_not_reported_as_applied_blocks_every_path_rule()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        await PolicyAsync(w, new AccessOsRules(["/work"], [], []), applied: false);

        var response = await RememberReadAsync(w, ReadPath);

        await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Conflict, CommandEndpoints.RuleOutsideAccess);
    }

    [Fact]
    public async Task A_policy_the_machine_reported_at_an_older_version_blocks_every_path_rule()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        await PolicyAsync(w, new AccessOsRules(["/work"], [], []), version: 2, reported: 1);

        var response = await RememberReadAsync(w, ReadPath);

        await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Conflict, CommandEndpoints.RuleOutsideAccess);
    }

    [Fact]
    public async Task A_policy_that_allows_the_projects_own_folder_and_lists_none_leaves_the_decision_to_the_agents_inside_mark_but_still_honours_its_denied_folders()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        await PolicyAsync(w, new AccessOsRules([], ["/work/secret"], []), includeProjectFolder: true);

        var allowed = await RememberReadAsync(w, ReadPath);
        var denied = await RememberReadAsync(w, "/work/secret/key.txt");

        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        await RemoteKit.AssertProblemAsync(denied, HttpStatusCode.Conflict, CommandEndpoints.RuleOutsideAccess);
    }

    [Fact]
    public async Task A_policy_that_lists_no_allowed_folder_and_leaves_out_the_projects_own_is_no_folder_limit_as_on_the_agent_but_still_honours_its_denied_folders()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        await PolicyAsync(w, new AccessOsRules([], ["/work/secret"], []), includeProjectFolder: false);

        var allowed = await RememberReadAsync(w, ReadPath);
        var denied = await RememberReadAsync(w, "/work/secret/key.txt");

        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        await RemoteKit.AssertProblemAsync(denied, HttpStatusCode.Conflict, CommandEndpoints.RuleOutsideAccess);
    }

    [Theory]
    [InlineData(OsKinds.Windows, @"C:\Work\Shop\a.txt", true)]
    [InlineData(OsKinds.Windows, "c:/work/shop/a.txt", true)]
    [InlineData(OsKinds.Windows, @"C:\Work\Shopping\a.txt", false)]
    [InlineData(OsKinds.Windows, "c:/work/shop/SECRET/key.txt", false)]
    [InlineData(OsKinds.Linux, "/work/shop/a.txt", true)]
    [InlineData(OsKinds.Linux, "/Work/Shop/a.txt", false)]
    [InlineData(OsKinds.Linux, "/work/shop/SECRET/key.txt", true)]
    public async Task The_section_of_the_machines_os_decides_with_that_os_separators_and_case_rule(string os, string path, bool remembered)
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        await PolicyAsync(w, NoRules, linux: new AccessOsRules(["/work/shop"], ["/work/shop/secret"], []),
            windows: new AccessOsRules([@"C:\Work\Shop"], [@"C:\Work\Shop\secret"], []));
        await using (var db = api.Db())
        {
            var machineId = await db.Agents.Where(a => a.Id == w.AgentId).Select(a => a.MachineId).SingleAsync();
            await db.Machines.Where(m => m.Id == machineId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Os, os));
        }

        var response = await RememberReadAsync(w, path);

        Assert.Equal(remembered ? HttpStatusCode.NoContent : HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_cleared_policy_is_no_limit_so_the_agents_inside_mark_decides_again()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        await PolicyAsync(w, NoRules, applied: false, cleared: true);

        var response = await RememberReadAsync(w, ReadPath);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task A_policy_does_not_touch_a_shell_command()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        await PolicyAsync(w, new AccessOsRules(["/work"], [], []), applied: false);

        await ApprovalRuleKit.RememberAsync(w, "Bash", new { command = "make build" }, ApprovalDurations.OneHour);

        Assert.Equal("Bash", Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId)).ToolName);
    }

    // ---- acting -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_path_rule_allows_at_once_only_when_the_agent_again_found_the_path_inside()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        await ApprovalRuleKit.RememberAsync(w, "Read", ReadOf(ReadPath), ApprovalDurations.OneHour, accessScope: AccessScopes.Inside);

        var inside = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Read", ReadOf(ReadPath), inline: true, accessScope: AccessScopes.Inside);
        var outside = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Read", ReadOf(ReadPath), inline: true, accessScope: AccessScopes.Outside);
        var unsent = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Read", ReadOf(ReadPath), inline: true);
        var other = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Read", ReadOf("/work/src/b.txt"), inline: true, accessScope: AccessScopes.Inside);

        Assert.NotNull(inside.Answer);
        Assert.Null(outside.Answer);
        Assert.Null(unsent.Answer);
        Assert.Null(other.Answer);
    }

    [Fact]
    public async Task A_path_rule_stops_acting_when_a_policy_that_the_machine_has_not_applied_now_covers_the_project_and_acts_again_once_it_is_applied()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        await ApprovalRuleKit.RememberAsync(w, "Read", ReadOf(ReadPath), ApprovalDurations.OneHour, accessScope: AccessScopes.Inside);
        await PolicyAsync(w, new AccessOsRules(["/work"], [], []), applied: false);

        var before = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Read", ReadOf(ReadPath), inline: true, accessScope: AccessScopes.Inside);
        await using (var db = api.Db())
        {
            var projectId = await db.ProjectAccessPolicies.Where(p => p.WorkspaceId == w.WorkspaceId).Select(p => p.ProjectId).SingleAsync();
            var agent = await db.Agents.SingleAsync(a => a.Id == w.AgentId);
            agent.AccessReport = AccessReports.Store(new AccessReport(new AccessCeilingReport([], [], [], true), [new AccessAppliedReport(projectId, 1, AccessPolicyStates.Applied, false, null)]));
            await db.SaveChangesAsync();
        }

        var after = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Read", ReadOf(ReadPath), inline: true, accessScope: AccessScopes.Inside);

        Assert.Null(before.Answer);
        Assert.NotNull(after.Answer);
    }
}
