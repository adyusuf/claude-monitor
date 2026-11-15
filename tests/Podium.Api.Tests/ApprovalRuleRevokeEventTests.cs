using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>Revoking an approval rule tells the workspace stream (F-197): the approvals page in another tab reads the list again on it (F-091, F-164).</summary>
[Collection(ApiGroup.Name)]
public sealed class ApprovalRuleRevokeEventTests(ApiFactory api)
{
    [Fact]
    public async Task Revoking_a_rule_publishes_one_approval_rule_event_with_its_id()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "rev-ev");
        var rule = await ApprovalRuleKit.SeedAsync(api, w, "make build");
        var other = await ApprovalRuleKit.SeedAsync(api, w, "make test");
        using var stream = await RemoteKit.OpenAsync(w.Owner.Http, $"/api/workspaces/{w.WorkspaceId}/stream");
        await RemoteKit.NextEventAsync(stream, "ready");

        (await w.Owner.SendAsync(HttpMethod.Delete, $"/api/approval-rules/{rule.Id}")).EnsureSuccessStatusCode();
        (await w.Owner.SendAsync(HttpMethod.Delete, $"/api/approval-rules/{other.Id}")).EnsureSuccessStatusCode();

        var first = await RemoteKit.NextEventAsync(stream, "approval_rule");
        var second = await RemoteKit.NextEventAsync(stream, "approval_rule");
        Assert.Equal(rule.Id, first.GetProperty("id").GetGuid());
        Assert.Equal(other.Id, second.GetProperty("id").GetGuid()); // one message per revoke, none for the 404 of a repeat
    }

    [Fact]
    public async Task Revoking_a_rule_that_is_already_revoked_publishes_nothing()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "rev-ev2");
        var rule = await ApprovalRuleKit.SeedAsync(api, w, "make build");
        var marker = await ApprovalRuleKit.SeedAsync(api, w, "make test");
        (await w.Owner.SendAsync(HttpMethod.Delete, $"/api/approval-rules/{rule.Id}")).EnsureSuccessStatusCode();
        using var stream = await RemoteKit.OpenAsync(w.Owner.Http, $"/api/workspaces/{w.WorkspaceId}/stream");
        await RemoteKit.NextEventAsync(stream, "ready");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await w.Owner.SendAsync(HttpMethod.Delete, $"/api/approval-rules/{rule.Id}")).StatusCode);
        (await w.Owner.SendAsync(HttpMethod.Delete, $"/api/approval-rules/{marker.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(marker.Id, (await RemoteKit.NextEventAsync(stream, "approval_rule")).GetProperty("id").GetGuid()); // the first event is the marker's, not the repeat's
    }
}
