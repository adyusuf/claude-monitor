using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Tests;

/// <summary>What the machine list says about updates, and the workspace's update mode (settings, the agent's view, the audit).</summary>
[Collection(ApiGroup.Name)]
public sealed class UpdateSettingsTests : IDisposable
{
    private readonly ApiFactory api;
    private readonly ManifestFile manifest;

    public UpdateSettingsTests(ApiFactory api)
    {
        this.api = api;
        manifest = new ManifestFile(api);
    }

    public void Dispose() => manifest.Dispose();

    private void Publish(params Dictionary<string, string?>[] entries) => manifest.Publish(entries);

    // ---- the machine list ----

    private static async Task<JsonElement> RowAsync(TestUser user) =>
        (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/agents"))[0];

    [Theory]
    [InlineData("0.3.1", true)]
    [InlineData("0.10.0", true)]
    [InlineData("0.3.0", false)]
    [InlineData("0.2.5", false)]
    public async Task The_machine_list_says_whether_a_newer_build_is_offered(string offered, bool expected)
    {
        var user = await api.NewClient().SignedUpAsync("upd-list");
        await user.ConnectAgentAsync(); // registers as macos / arm64 / 0.3.0
        Publish(ManifestFile.Entry(offered));
        var row = await RowAsync(user);
        Assert.Equal("0.3.0", row.GetProperty("version").GetString());
        Assert.Equal(expected, row.GetProperty("updateAvailable").GetBoolean());
        Assert.Equal(offered, row.GetProperty("latestVersion").GetString());
    }

    [Fact]
    public async Task The_machine_list_without_an_offer_has_no_update_info()
    {
        var user = await api.NewClient().SignedUpAsync("upd-list-none");
        await user.ConnectAgentAsync();
        var row = await RowAsync(user);
        Assert.False(row.GetProperty("updateAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("latestVersion").ValueKind);

        Publish(ManifestFile.Entry("0.3.1", os: "windows", arch: "x64"));
        row = await RowAsync(user);
        Assert.False(row.GetProperty("updateAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("latestVersion").ValueKind);
    }

    [Fact]
    public async Task A_revoked_agent_row_carries_no_update_info()
    {
        var user = await api.NewClient().SignedUpAsync("upd-list-revoked");
        var agent = await user.ConnectAgentAsync();
        Publish(ManifestFile.Entry("0.3.1"));
        Assert.True((await RowAsync(user)).GetProperty("updateAvailable").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync($"/api/agents/{agent.Tokens.AgentId}/revoke")).StatusCode);
        var row = await RowAsync(user);
        Assert.NotEqual("active", row.GetProperty("status").GetString());
        Assert.False(row.GetProperty("updateAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("latestVersion").ValueKind);
    }

    [Fact]
    public async Task The_machine_list_picks_the_offer_of_each_machines_own_os_and_cpu()
    {
        var user = await api.NewClient().SignedUpAsync("upd-list-two");
        await user.ConnectAgentAsync();
        Publish(ManifestFile.Entry("0.3.1"), ManifestFile.Entry("0.9.0", os: "windows", arch: "x64", file: "cm-agent-windows-x64.zip"));
        Assert.Equal("0.3.1", (await RowAsync(user)).GetProperty("latestVersion").GetString());
    }

    // ---- the workspace's update mode ----

    private async Task<(TestUser Owner, TestUser Member)> TeamAsync()
    {
        var owner = await api.NewClient().SignedUpAsync("upd-owner");
        var member = await api.NewClient().SignedUpAsync("upd-member");
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = member.Email, role = "member" })).EnsureSuccessStatusCode();
        (await member.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(member.Email) })).EnsureSuccessStatusCode();
        return (owner, member);
    }

    /// <summary>The agentUpdate inside detail.before / detail.after (the detail is stored as the server serialised it; the key's case is not the point).</summary>
    private static string? Mode(JsonElement detail, string side)
    {
        var settings = detail.EnumerateObject().Single(p => string.Equals(p.Name, side, StringComparison.OrdinalIgnoreCase)).Value;
        return settings.EnumerateObject().Single(p => string.Equals(p.Name, "agentUpdate", StringComparison.OrdinalIgnoreCase)).Value.GetString();
    }

    private static async Task<string?> ModeOf(TestUser user) =>
        (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}")).GetProperty("settings").GetProperty("agentUpdate").GetString();

    [Fact]
    public async Task A_new_workspace_does_not_update_agents_and_the_mode_persists_to_the_agent()
    {
        var owner = await api.NewClient().SignedUpAsync("upd-mode");
        var agent = await owner.ConnectAgentAsync();
        var url = $"/api/workspaces/{owner.WorkspaceId}/settings";
        Assert.Equal(UpdateModes.Off, await ModeOf(owner));
        Assert.Equal(UpdateModes.Off, (await agent.Http.GetFromJsonAsync<AgentSettings>("/api/agent/settings", TestUser.Json))!.AgentUpdate);

        foreach (var mode in new[] { UpdateModes.Check, UpdateModes.On, UpdateModes.Off, UpdateModes.On })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Put, url, new { agentUpdate = mode })).StatusCode);
            Assert.Equal(mode, await ModeOf(owner));
            Assert.Equal(mode, (await agent.Http.GetFromJsonAsync<AgentSettings>("/api/agent/settings", TestUser.Json))!.AgentUpdate);
        }
    }

    [Fact]
    public async Task An_invalid_mode_is_a_400_on_the_field_and_changes_nothing()
    {
        var owner = await api.NewClient().SignedUpAsync("upd-invalid");
        var url = $"/api/workspaces/{owner.WorkspaceId}/settings";
        (await owner.SendAsync(HttpMethod.Put, url, new { agentUpdate = "check" })).EnsureSuccessStatusCode();
        foreach (var bad in new[] { "always", "ON", "", " check", "true" })
        {
            var response = await owner.SendAsync(HttpMethod.Put, url, new { agentUpdate = bad, retentionDays = 7 });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("agentUpdate", out _));
        }

        Assert.Equal("check", await ModeOf(owner));
        var retention = (await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}")).GetProperty("settings").GetProperty("retentionDays").GetInt32();
        Assert.Equal(90, retention);
    }

    [Fact]
    public async Task Leaving_the_mode_out_keeps_it_and_changing_it_keeps_the_other_settings()
    {
        var owner = await api.NewClient().SignedUpAsync("upd-omit");
        var url = $"/api/workspaces/{owner.WorkspaceId}/settings";
        (await owner.SendAsync(HttpMethod.Put, url, new { agentUpdate = "on" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Put, url, new { retentionDays = 30 })).StatusCode);
        Assert.Equal("on", await ModeOf(owner));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(HttpMethod.Put, url, new { agentUpdate = "check" })).StatusCode);
        var s = (await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}")).GetProperty("settings");
        Assert.Equal(30, s.GetProperty("retentionDays").GetInt32());
        Assert.Equal("check", s.GetProperty("agentUpdate").GetString());
    }

    [Fact]
    public async Task Only_an_admin_changes_the_mode_and_a_refused_attempt_changes_nothing()
    {
        var (owner, member) = await TeamAsync();
        var url = $"/api/workspaces/{owner.WorkspaceId}/settings";
        Assert.Equal(HttpStatusCode.NotFound, (await member.SendAsync(HttpMethod.Put, url, new { agentUpdate = "on" })).StatusCode);
        Assert.Equal(UpdateModes.Off, await ModeOf(owner));
        Assert.Equal(UpdateModes.Off, (await member.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}")).GetProperty("settings").GetProperty("agentUpdate").GetString());
        var stranger = await api.NewClient().SignedUpAsync("upd-stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(HttpMethod.Put, url, new { agentUpdate = "on" })).StatusCode);
    }

    [Fact]
    public async Task The_audit_entry_for_a_settings_change_shows_the_mode_before_and_after()
    {
        var owner = await api.NewClient().SignedUpAsync("upd-audit");
        var url = $"/api/workspaces/{owner.WorkspaceId}/settings";
        (await owner.SendAsync(HttpMethod.Put, url, new { agentUpdate = "check" })).EnsureSuccessStatusCode();
        (await owner.SendAsync(HttpMethod.Put, url, new { agentUpdate = "on" })).EnsureSuccessStatusCode();
        var items = (await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/audit")).GetProperty("items").EnumerateArray()
            .Where(a => a.GetProperty("action").GetString() == "workspace.settings_changed")
            .Select(a => (Before: Mode(a.GetProperty("detail"), "before"), After: Mode(a.GetProperty("detail"), "after")))
            .ToList();
        Assert.Contains(("off", "check"), items);
        Assert.Contains(("check", "on"), items);
    }
}
