using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// Counting a rule's use is a conditional write in the database (F-162), like a grant's: requests at the same moment all count, and a rule revoked
/// a moment before the count allows nothing.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class ApprovalRuleUseRaceTests(ApiFactory api)
{
    private static readonly object Make = new { command = "make build" };

    private static Func<Task<HttpResponseMessage>> Inline(RuleWorld w) => async () =>
    {
        var created = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", Make, inline: true);
        return new HttpResponseMessage(created.Answer is null ? System.Net.HttpStatusCode.Accepted : System.Net.HttpStatusCode.OK);
    };

    private async Task<(RuleWorld World, Guid RuleId)> RememberedAsync(string tag)
    {
        var w = await ApprovalRuleKit.WorldAsync(api, tag);
        await ApprovalRuleKit.RememberAsync(w, "Bash", Make, ApprovalDurations.OneHour);
        return (w, Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId)).Id);
    }

    [Fact]
    public async Task Requests_at_the_same_moment_all_count_their_use_of_the_rule()
    {
        var (w, ruleId) = await RememberedAsync("use-par");

        var answers = await RowLockHold.RunAsync(api, $"SELECT 1 FROM approval_rules WHERE id = {ruleId} FOR UPDATE", Inline(w), Inline(w), Inline(w), Inline(w));

        Assert.All(answers, a => Assert.Equal(System.Net.HttpStatusCode.OK, a.StatusCode));
        Assert.Equal(4, Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId)).UseCount);
    }

    [Fact]
    public async Task A_rule_revoked_before_its_use_is_written_allows_nothing_and_leaves_the_request_open()
    {
        var (w, ruleId) = await RememberedAsync("use-rev");
        var now = api.Clock.GetUtcNow();
        // The revoke lands after the request has matched the rule and before the use is written.
        var fault = new CommandFault("UPDATE approval_rules")
        {
            Before = () =>
            {
                using var db = api.Db();
                db.Database.ExecuteSql($"UPDATE approval_rules SET revoked_at = {now} WHERE id = {ruleId}");
            },
        };
        await using var host = HostKit.WithInterceptor(api, fault);
        using var agentHttp = host.CreateClient();
        foreach (var header in w.Agent.Http.DefaultRequestHeaders) agentHttp.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);

        var response = await agentHttp.PostAsJsonAsync("/api/agent/permission-requests",
            new PermissionRequestCreate(HarnessKinds.ClaudeCode, w.Session, "Bash", JsonSerializer.SerializeToElement(Make), 120, true, null), TestUser.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<PermissionRequestCreated>(TestUser.Json))!;
        Assert.Null(created.Answer); // no inline answer: the owner is asked
        Assert.Equal(PermissionStatuses.Open, (await ApprovalRuleKit.RequestRowAsync(api, created.Id)).Status);
        Assert.Equal(0, Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId)).UseCount);
        Assert.Empty(await ApprovalRuleKit.AuditAsync(api, w.WorkspaceId, AuditActions.PermissionAutoAllowed));
        Assert.Equal(SessionStatuses.Waiting, await ApprovalRuleKit.SessionStatusAsync(api, w.Agent, w.Session));
    }
}
