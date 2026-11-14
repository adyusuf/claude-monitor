using Podium.Api.Background;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>A rule holds a command a session ran, so it does not outlive its use (ADR-0019 §9b): ended rules go a week after they end, unused ones with the workspace's retention.</summary>
[Collection(ApiGroup.Name)]
public sealed class ApprovalRulePurgeTests(ApiFactory api)
{
    private static readonly TimeSpan Week = ApprovalRuleLimits.KeepEnded;

    private async Task<List<string?>> CommandsAsync(RuleWorld w) => (await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId)).Select(r => r.Command).Order().ToList();

    [Fact]
    public async Task The_housekeeper_deletes_rules_that_ended_or_were_revoked_a_week_ago_and_keeps_the_rest()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var now = api.Clock.GetUtcNow();
        var longAgo = now - Week - TimeSpan.FromDays(1);
        var recently = now - Week + TimeSpan.FromDays(1);
        await ApprovalRuleKit.SeedAsync(api, w, "expired long ago", ApprovalDurations.OneHour, createdAt: longAgo - TimeSpan.FromHours(1), expiresAt: longAgo);
        await ApprovalRuleKit.SeedAsync(api, w, "expired exactly a week ago", ApprovalDurations.OneHour, createdAt: now - Week - TimeSpan.FromHours(1), expiresAt: now - Week);
        await ApprovalRuleKit.SeedAsync(api, w, "expired recently", ApprovalDurations.OneHour, createdAt: recently - TimeSpan.FromHours(1), expiresAt: recently);
        await ApprovalRuleKit.SeedAsync(api, w, "revoked long ago", revokedAt: longAgo);
        await ApprovalRuleKit.SeedAsync(api, w, "revoked exactly a week ago", revokedAt: now - Week);
        await ApprovalRuleKit.SeedAsync(api, w, "revoked recently", revokedAt: recently);
        await ApprovalRuleKit.SeedAsync(api, w, "live always");
        await ApprovalRuleKit.SeedAsync(api, w, "live for an hour", ApprovalDurations.OneHour);

        await using (var db = api.Db()) await Housekeeper.RunOnceAsync(db, api.Clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal(new[] { "expired recently", "live always", "live for an hour", "revoked recently" }, await CommandsAsync(w));
    }

    [Fact]
    public async Task The_retention_pass_deletes_a_rule_nobody_used_within_the_workspaces_retention_even_a_live_always_rule_and_keeps_a_used_one()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        (await w.Owner.SendAsync(HttpMethod.Put, $"/api/workspaces/{w.WorkspaceId}/settings", new { retentionDays = 1 })).EnsureSuccessStatusCode();
        var now = api.Clock.GetUtcNow();
        await ApprovalRuleKit.SeedAsync(api, w, "never used", createdAt: now - TimeSpan.FromDays(2));
        await ApprovalRuleKit.SeedAsync(api, w, "used long ago", createdAt: now - TimeSpan.FromDays(5), lastUsedAt: now - TimeSpan.FromDays(2));
        await ApprovalRuleKit.SeedAsync(api, w, "used an hour ago", createdAt: now - TimeSpan.FromDays(5), lastUsedAt: now - TimeSpan.FromHours(1));
        await ApprovalRuleKit.SeedAsync(api, w, "made hours ago", createdAt: now - TimeSpan.FromHours(12));

        await RemoteKit.HousekeepAsync(api);

        Assert.Equal(new[] { "made hours ago", "used an hour ago" }, await CommandsAsync(w));
    }

    [Fact]
    public async Task The_retention_pass_leaves_the_rules_of_a_workspace_whose_retention_is_longer()
    {
        var w = await ApprovalRuleKit.WorldAsync(api);
        var now = api.Clock.GetUtcNow();
        await ApprovalRuleKit.SeedAsync(api, w, "unused for ten days", createdAt: now - TimeSpan.FromDays(10));

        await RemoteKit.HousekeepAsync(api);

        Assert.Equal(new[] { "unused for ten days" }, await CommandsAsync(w));
    }
}
