using System.Text.Json;
using Podium.Api.Approvals;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary><see cref="ApprovalRules.MatchAsync"/> on its own, with the machine's row changed in memory, for what the endpoints cannot reach (a rule still live on a machine that is strict or not active).</summary>
[Collection(ApiGroup.Name)]
public sealed class ApprovalRuleMatchTests(ApiFactory api)
{
    private async Task<(RuleWorld World, ApprovalRule Rule)> RememberedAsync()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        await ApprovalRuleKit.RememberAsync(w, "Bash", new { command = "make build" }, ApprovalDurations.OneHour);
        return (w, Assert.Single(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId)));
    }

    private async Task<ApprovalRule?> MatchAsync(RuleWorld w, string tool, string json, Action<Agent>? change = null)
    {
        await using var db = api.Db();
        var agent = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == w.AgentId);
        change?.Invoke(agent);
        using var input = JsonDocument.Parse(json);
        return await ApprovalRules.MatchAsync(db, agent, await ApprovalRuleKit.RepoIdAsync(api, w.WorkspaceId), tool, input.RootElement, null, api.Clock.GetUtcNow(), CancellationToken.None);
    }

    [Fact]
    public async Task Matching_finds_the_live_rule_of_the_scope()
    {
        var (w, rule) = await RememberedAsync();

        var found = await MatchAsync(w, "Bash", """{"command":"make build"}""");

        Assert.Equal(rule.Id, found?.Id);
    }

    [Fact]
    public async Task Matching_finds_nothing_for_a_machine_that_is_not_active_even_with_a_live_rule()
    {
        var (w, _) = await RememberedAsync();

        var found = await MatchAsync(w, "Bash", """{"command":"make build"}""", agent => agent.Status = AgentStatuses.Revoked);

        Assert.Null(found);
    }

    [Theory]
    [InlineData("production", null, null, null)]
    [InlineData(null, "production", null, null)]
    [InlineData(null, null, "database", null)]
    [InlineData(null, null, null, "database")]
    public async Task Matching_finds_nothing_for_a_strict_machine_even_when_a_live_rule_is_still_there(string? environment, string? claimedEnvironment, string? function, string? claimedFunction)
    {
        var (w, _) = await RememberedAsync();

        var found = await MatchAsync(w, "Bash", """{"command":"make build"}""", agent =>
        {
            agent.RoleEnvironment = environment;
            agent.ClaimedEnvironment = claimedEnvironment;
            agent.RoleFunction = function;
            agent.ClaimedFunction = claimedFunction;
        });

        Assert.Null(found);
    }

    [Fact]
    public async Task Matching_finds_nothing_for_a_tool_outside_the_allowlist_even_if_a_row_pins_the_same_text()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        await ApprovalRuleKit.SeedAsync(api, w, "https://example.invalid/", tool: "WebFetch");

        var found = await MatchAsync(w, "WebFetch", """{"url":"https://example.invalid/"}""");

        Assert.Null(found);
    }

    [Fact]
    public async Task Matching_finds_nothing_when_the_rule_was_made_in_another_workspace_than_the_machines()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var rule = ApprovalRuleKit.Build(api, w, await ApprovalRuleKit.RepoIdAsync(api, w.WorkspaceId), "make build");
        rule.WorkspaceId = Guid.CreateVersion7();
        ApprovalRuleKit.SeedNow(api, rule);

        var found = await MatchAsync(w, "Bash", """{"command":"make build"}""");

        Assert.Null(found);
    }
}
