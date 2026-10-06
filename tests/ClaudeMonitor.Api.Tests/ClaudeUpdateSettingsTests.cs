using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The workspace's claudeUpdate setting (may agents update Claude Code on their machines): default, persistence, independence from agentUpdate, who may change it, the agent's view, the audit.</summary>
[Collection(ApiGroup.Name)]
public sealed class ClaudeUpdateSettingsTests(ApiFactory api)
{
    private static string Url(TestUser user) => $"/api/workspaces/{user.WorkspaceId}/settings";

    private static async Task<JsonElement> SettingsOf(TestUser user) =>
        (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}")).GetProperty("settings");

    private static async Task<bool> ClaudeUpdateOf(TestUser user) => (await SettingsOf(user)).GetProperty("claudeUpdate").GetBoolean();

    /// <summary>What a member of another person's workspace reads there (user.WorkspaceId is the user's own).</summary>
    private static async Task<bool> ClaudeUpdateSeenBy(TestUser user, TestUser owner) =>
        (await user.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}")).GetProperty("settings").GetProperty("claudeUpdate").GetBoolean();

    private static async Task<AgentSettings> AgentViewOf(TestAgent agent) =>
        (await agent.Http.GetFromJsonAsync<AgentSettings>("/api/agent/settings", TestUser.Json))!;

    private static async Task Put(TestUser user, object body) =>
        Assert.Equal(HttpStatusCode.NoContent, (await user.SendAsync(HttpMethod.Put, Url(user), body)).StatusCode);

    private async Task<(TestUser Owner, TestUser Other)> TeamAsync(string role)
    {
        var owner = await api.NewClient().SignedUpAsync("cu-owner");
        var other = await api.NewClient().SignedUpAsync("cu-other");
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = other.Email, role })).EnsureSuccessStatusCode();
        (await other.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(other.Email) })).EnsureSuccessStatusCode();
        return (owner, other);
    }

    /// <summary>The claudeUpdate inside detail.before / detail.after (the stored detail is PascalCase; the key's case is not the point).</summary>
    private static bool? Flag(JsonElement detail, string side)
    {
        var settings = detail.EnumerateObject().Single(p => string.Equals(p.Name, side, StringComparison.OrdinalIgnoreCase)).Value;
        var value = settings.EnumerateObject().Single(p => string.Equals(p.Name, "claudeUpdate", StringComparison.OrdinalIgnoreCase)).Value;
        return value.ValueKind == JsonValueKind.Null ? null : value.GetBoolean();
    }

    private static async Task<List<(bool? Before, bool? After)>> AuditedChanges(TestUser user) =>
        (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/audit")).GetProperty("items").EnumerateArray()
            .Where(a => a.GetProperty("action").GetString() == "workspace.settings_changed")
            .Select(a => (Flag(a.GetProperty("detail"), "before"), Flag(a.GetProperty("detail"), "after")))
            .ToList();

    // ---- default ----

    [Fact]
    public async Task A_new_workspace_does_not_update_claude_code_and_says_so_to_the_web_and_the_agent()
    {
        var owner = await api.NewClient().SignedUpAsync("cu-default");
        var agent = await owner.ConnectAgentAsync();
        Assert.False(await ClaudeUpdateOf(owner));
        Assert.False((await AgentViewOf(agent)).ClaudeUpdate);
    }

    [Fact]
    public void The_entity_and_the_contract_default_to_off()
    {
        Assert.False(new WorkspaceSettings().ClaudeUpdate);
        Assert.False(new AgentSettings(true, 1, Guid.Empty).ClaudeUpdate);
    }

    [Fact]
    public async Task The_database_default_of_the_column_is_off_which_is_what_rows_that_existed_before_the_migration_get()
    {
        var owner = await api.NewClient().SignedUpAsync("cu-dbdefault");
        await Put(owner, new { claudeUpdate = true });
        Assert.True(await ClaudeUpdateOf(owner));

        await using (var db = api.Db())
            await db.Database.ExecuteSqlRawAsync("UPDATE workspace_settings SET claude_update = DEFAULT WHERE workspace_id = {0}", owner.WorkspaceId);

        Assert.False(await ClaudeUpdateOf(owner));
    }

    // ---- persistence ----

    [Fact]
    public async Task Turning_it_on_persists_to_the_web_and_the_agent_and_turning_it_off_takes_it_back()
    {
        var owner = await api.NewClient().SignedUpAsync("cu-toggle");
        var agent = await owner.ConnectAgentAsync();

        foreach (var wanted in new[] { true, false, true, false })
        {
            await Put(owner, new { claudeUpdate = wanted });
            Assert.Equal(wanted, await ClaudeUpdateOf(owner));
            Assert.Equal(wanted, (await AgentViewOf(agent)).ClaudeUpdate);
        }
    }

    [Fact]
    public async Task A_member_sees_the_value_but_a_stranger_does_not_reach_it()
    {
        var (owner, member) = await TeamAsync("member");
        await Put(owner, new { claudeUpdate = true });
        Assert.True(await ClaudeUpdateSeenBy(member, owner));
        var stranger = await api.NewClient().SignedUpAsync("cu-stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/workspaces/{owner.WorkspaceId}")).StatusCode);
    }

    // ---- independence ----

    [Fact]
    public async Task Leaving_the_field_out_keeps_it_whichever_value_it_has()
    {
        var owner = await api.NewClient().SignedUpAsync("cu-omit");
        var agent = await owner.ConnectAgentAsync();

        await Put(owner, new { retentionDays = 30 });
        Assert.False(await ClaudeUpdateOf(owner));

        await Put(owner, new { claudeUpdate = true });
        await Put(owner, new { retentionDays = 45 });
        await Put(owner, new { maskSecrets = false, eventMaxBytes = 300_000 });
        await Put(owner, new { });
        var s = await SettingsOf(owner);
        Assert.True(s.GetProperty("claudeUpdate").GetBoolean());
        Assert.Equal(45, s.GetProperty("retentionDays").GetInt32());
        Assert.False(s.GetProperty("maskSecrets").GetBoolean());
        Assert.True((await AgentViewOf(agent)).ClaudeUpdate);
    }

    [Fact]
    public async Task Changing_claude_update_keeps_the_other_settings()
    {
        var owner = await api.NewClient().SignedUpAsync("cu-keep-others");
        await Put(owner, new { retentionDays = 30, agentUpdate = "check", maskSecrets = false, eventMaxBytes = 300_000 });
        await Put(owner, new { claudeUpdate = true });
        var s = await SettingsOf(owner);
        Assert.Equal(30, s.GetProperty("retentionDays").GetInt32());
        Assert.Equal("check", s.GetProperty("agentUpdate").GetString());
        Assert.False(s.GetProperty("maskSecrets").GetBoolean());
        Assert.Equal(300_000, s.GetProperty("eventMaxBytes").GetInt32());
        Assert.True(s.GetProperty("claudeUpdate").GetBoolean());
    }

    [Fact]
    public async Task The_agent_update_mode_and_claude_update_do_not_reset_each_other()
    {
        var owner = await api.NewClient().SignedUpAsync("cu-independent");
        var agent = await owner.ConnectAgentAsync();

        await Put(owner, new { claudeUpdate = true });
        await Put(owner, new { agentUpdate = "on" });
        var view = await AgentViewOf(agent);
        Assert.True(view.ClaudeUpdate);
        Assert.Equal(UpdateModes.On, view.AgentUpdate);

        await Put(owner, new { agentUpdate = "off" });
        Assert.True(await ClaudeUpdateOf(owner));

        await Put(owner, new { agentUpdate = "check" });
        await Put(owner, new { claudeUpdate = false });
        view = await AgentViewOf(agent);
        Assert.False(view.ClaudeUpdate);
        Assert.Equal(UpdateModes.Check, view.AgentUpdate);
        Assert.Equal("check", (await SettingsOf(owner)).GetProperty("agentUpdate").GetString());
    }

    [Fact]
    public async Task A_body_as_the_web_sent_before_the_field_existed_still_works_and_leaves_it_alone()
    {
        var owner = await api.NewClient().SignedUpAsync("cu-oldclient");
        await Put(owner, new { claudeUpdate = true });
        await Put(owner, new { maskSecrets = true, retentionDays = 60, eventMaxBytes = 262_144, agentUpdate = "check" });
        var s = await SettingsOf(owner);
        Assert.Equal(60, s.GetProperty("retentionDays").GetInt32());
        Assert.Equal("check", s.GetProperty("agentUpdate").GetString());
        Assert.True(s.GetProperty("claudeUpdate").GetBoolean());
    }

    [Fact]
    public async Task A_rejected_request_does_not_change_claude_update()
    {
        var owner = await api.NewClient().SignedUpAsync("cu-rejected");
        var response = await owner.SendAsync(HttpMethod.Put, Url(owner), new { claudeUpdate = true, retentionDays = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await ClaudeUpdateOf(owner));
    }

    // ---- who may change it ----

    [Theory]
    [InlineData("member")]
    [InlineData("viewer")]
    public async Task Only_an_admin_changes_it_and_a_refused_attempt_changes_nothing(string role)
    {
        var (owner, other) = await TeamAsync(role);
        Assert.Equal(HttpStatusCode.NotFound, (await other.SendAsync(HttpMethod.Put, Url(owner), new { claudeUpdate = true })).StatusCode);
        Assert.False(await ClaudeUpdateOf(owner));

        await Put(owner, new { claudeUpdate = true });
        Assert.Equal(HttpStatusCode.NotFound, (await other.SendAsync(HttpMethod.Put, Url(owner), new { claudeUpdate = false })).StatusCode);
        Assert.True(await ClaudeUpdateOf(owner));
        Assert.True(await ClaudeUpdateSeenBy(other, owner));
        Assert.Single(await AuditedChanges(owner));
    }

    [Fact]
    public async Task A_stranger_cannot_change_it()
    {
        var owner = await api.NewClient().SignedUpAsync("cu-owner2");
        var stranger = await api.NewClient().SignedUpAsync("cu-stranger2");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(HttpMethod.Put, Url(owner), new { claudeUpdate = true })).StatusCode);
        Assert.False(await ClaudeUpdateOf(owner));
    }

    [Fact]
    public async Task An_admin_changes_it()
    {
        var (owner, admin) = await TeamAsync("admin");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Put, Url(owner), new { claudeUpdate = true })).StatusCode);
        Assert.True(await ClaudeUpdateOf(owner));
    }

    // ---- the audit ----

    [Fact]
    public async Task The_audit_entry_for_a_settings_change_shows_claude_update_before_and_after()
    {
        var owner = await api.NewClient().SignedUpAsync("cu-audit");
        await Put(owner, new { claudeUpdate = true });
        await Put(owner, new { retentionDays = 20 }); // untouched: before and after both true
        await Put(owner, new { claudeUpdate = false });
        var items = await AuditedChanges(owner);
        Assert.Equal(3, items.Count);
        Assert.Contains((false, true), items);
        Assert.Contains((true, true), items);
        Assert.Contains((true, false), items);
    }
}
