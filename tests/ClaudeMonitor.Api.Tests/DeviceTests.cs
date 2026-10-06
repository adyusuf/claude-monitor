using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Tests;

[Collection(ApiGroup.Name)]
public sealed class DeviceTests(ApiFactory api)
{
    private static DeviceCodeRequest Request(string version = "0.3.0", string os = OsKinds.Windows) =>
        new("machine-" + Guid.NewGuid().ToString("N"), "desk-7", os, "x64", version);

    private static async Task<string> ErrorOf(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<DeviceTokenError>(TestUser.Json))!.Error;

    [Fact]
    public async Task The_device_flow_answers_pending_slow_down_then_tokens_once()
    {
        var user = await api.NewClient().SignedUpAsync("device");
        var agent = api.CreateClient();
        var code = (await (await agent.PostAsJsonAsync("/api/device/code", Request(), TestUser.Json))
            .Content.ReadFromJsonAsync<DeviceCodeResponse>(TestUser.Json))!;
        Assert.Matches("^[A-Z]{4}-[A-Z]{4}$", code.UserCode);
        Assert.Equal(ApiFactory.Origin + "/device", code.VerificationUri);

        var poll = new DeviceTokenRequest(code.DeviceCode);
        Assert.Equal(DeviceTokenErrors.Pending, await ErrorOf(await agent.PostAsJsonAsync("/api/device/token", poll, TestUser.Json)));
        Assert.Equal(DeviceTokenErrors.SlowDown, await ErrorOf(await agent.PostAsJsonAsync("/api/device/token", poll, TestUser.Json)));

        var lookup = await user.GetJsonAsync("/api/device/lookup/" + code.UserCode.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant());
        Assert.Equal("desk-7", lookup.GetProperty("hostname").GetString());
        (await user.PostAsync("/api/device/approve", new { userCode = code.UserCode, workspaceId = user.WorkspaceId })).EnsureSuccessStatusCode();
        api.Clock.Advance(TimeSpan.FromSeconds(6));
        var tokens = await (await agent.PostAsJsonAsync("/api/device/token", poll, TestUser.Json)).Content.ReadFromJsonAsync<TokenResponse>(TestUser.Json);
        Assert.Equal(user.WorkspaceId, tokens!.WorkspaceId);
        Assert.Equal(DeviceTokenErrors.Expired, await ErrorOf(await agent.PostAsJsonAsync("/api/device/token", poll, TestUser.Json)));

        var agents = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/agents");
        Assert.Equal("windows", agents[0].GetProperty("os").GetString());
        Assert.Equal("active", agents[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_denied_or_expired_code_never_yields_tokens()
    {
        var user = await api.NewClient().SignedUpAsync("deny");
        var agent = api.CreateClient();
        var denied = (await (await agent.PostAsJsonAsync("/api/device/code", Request(), TestUser.Json)).Content.ReadFromJsonAsync<DeviceCodeResponse>(TestUser.Json))!;
        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync("/api/device/deny", new { userCode = denied.UserCode, workspaceId = user.WorkspaceId })).StatusCode);
        Assert.Equal(DeviceTokenErrors.Denied, await ErrorOf(await agent.PostAsJsonAsync("/api/device/token", new DeviceTokenRequest(denied.DeviceCode), TestUser.Json)));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await user.PostAsync("/api/device/deny", new { userCode = denied.UserCode, workspaceId = user.WorkspaceId })).StatusCode);

        var late = (await (await agent.PostAsJsonAsync("/api/device/code", Request(), TestUser.Json)).Content.ReadFromJsonAsync<DeviceCodeResponse>(TestUser.Json))!;
        api.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await user.PostAsync("/api/device/approve", new { userCode = late.UserCode, workspaceId = user.WorkspaceId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync("/api/device/lookup/" + late.UserCode)).StatusCode);
        Assert.Equal(DeviceTokenErrors.Expired, await ErrorOf(await agent.PostAsJsonAsync("/api/device/token", new DeviceTokenRequest(late.DeviceCode), TestUser.Json)));
        Assert.Equal(DeviceTokenErrors.Expired, await ErrorOf(await agent.PostAsJsonAsync("/api/device/token", new DeviceTokenRequest(late.DeviceCode), TestUser.Json)));
        Assert.Equal(DeviceTokenErrors.Expired, await ErrorOf(await agent.PostAsJsonAsync("/api/device/token", new DeviceTokenRequest("nonsense"), TestUser.Json)));
    }

    [Fact]
    public async Task Approval_needs_membership_in_the_chosen_workspace()
    {
        var user = await api.NewClient().SignedUpAsync("approve");
        var stranger = await api.NewClient().SignedUpAsync("stranger");
        var code = (await (await api.CreateClient().PostAsJsonAsync("/api/device/code", Request(), TestUser.Json)).Content.ReadFromJsonAsync<DeviceCodeResponse>(TestUser.Json))!;
        Assert.Equal(HttpStatusCode.BadRequest,
            (await user.PostAsync("/api/device/approve", new { userCode = code.UserCode, workspaceId = stranger.WorkspaceId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsync("/api/device/approve", new { userCode = code.UserCode })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await user.PostAsync("/api/device/approve", new { userCode = "ZZZZ-ZZZZ", workspaceId = user.WorkspaceId })).StatusCode);
    }

    private async Task<DeviceCodeResponse> NewCodeAsync() =>
        (await (await api.CreateClient().PostAsJsonAsync("/api/device/code", Request(), TestUser.Json))
            .Content.ReadFromJsonAsync<DeviceCodeResponse>(TestUser.Json))!;

    private static async Task AssertNotAMemberAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("not_a_member", errors.GetProperty("workspaceId")[0].GetString());
    }

    [Fact]
    public async Task Denying_needs_a_workspace_the_caller_is_a_member_of()
    {
        var user = await api.NewClient().SignedUpAsync("deny-none");
        var code = await NewCodeAsync();
        await AssertNotAMemberAsync(await user.PostAsync("/api/device/deny", new { userCode = code.UserCode }));
        // A made-up workspace is no better than a missing one.
        await AssertNotAMemberAsync(await user.PostAsync("/api/device/deny", new { userCode = code.UserCode, workspaceId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.OK, (await user.Http.GetAsync("/api/device/lookup/" + code.UserCode)).StatusCode);
    }

    [Fact]
    public async Task A_stranger_cannot_deny_and_the_rightful_user_can_still_approve()
    {
        var rightful = await api.NewClient().SignedUpAsync("deny-rightful");
        var stranger = await api.NewClient().SignedUpAsync("deny-stranger");
        var agent = api.CreateClient();
        var code = await NewCodeAsync();
        await AssertNotAMemberAsync(await stranger.PostAsync("/api/device/deny", new { userCode = code.UserCode, workspaceId = rightful.WorkspaceId }));

        // Still pending: the poll says so, and the approval goes through.
        Assert.Equal(DeviceTokenErrors.Pending, await ErrorOf(await agent.PostAsJsonAsync("/api/device/token", new DeviceTokenRequest(code.DeviceCode), TestUser.Json)));
        Assert.Equal(HttpStatusCode.NoContent,
            (await rightful.PostAsync("/api/device/approve", new { userCode = code.UserCode, workspaceId = rightful.WorkspaceId })).StatusCode);
        await using var db = api.Db();
        Assert.False(db.AuditEvents.Any(a => a.ActorUserId == stranger.Id && a.Action == "agent.device_denied"));
    }

    [Fact]
    public async Task A_viewer_cannot_deny_but_a_member_can_and_the_audit_row_names_the_workspace()
    {
        var owner = await api.NewClient().SignedUpAsync("deny-owner");
        var viewer = await api.NewClient().SignedUpAsync("deny-viewer");
        var member = await api.NewClient().SignedUpAsync("deny-member");
        foreach (var (invited, role) in new[] { (viewer, "viewer"), (member, "member") })
        {
            (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = invited.Email, role })).EnsureSuccessStatusCode();
            (await invited.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(invited.Email) })).EnsureSuccessStatusCode();
        }

        var code = await NewCodeAsync();
        await AssertNotAMemberAsync(await viewer.PostAsync("/api/device/deny", new { userCode = code.UserCode, workspaceId = owner.WorkspaceId }));
        Assert.Equal(HttpStatusCode.NoContent,
            (await member.PostAsync("/api/device/deny", new { userCode = code.UserCode, workspaceId = owner.WorkspaceId })).StatusCode);
        Assert.Equal(DeviceTokenErrors.Denied,
            await ErrorOf(await api.CreateClient().PostAsJsonAsync("/api/device/token", new DeviceTokenRequest(code.DeviceCode), TestUser.Json)));
        await using var db = api.Db();
        var audit = Assert.Single(db.AuditEvents.Where(a => a.Action == "agent.device_denied" && a.ActorUserId == member.Id));
        Assert.Equal(owner.WorkspaceId, audit.WorkspaceId);
        Assert.False(db.AuditEvents.Any(a => a.Action == "agent.device_denied" && a.ActorUserId == viewer.Id));
    }

    [Fact]
    public async Task Denying_with_the_own_workspace_is_recorded_against_it()
    {
        var user = await api.NewClient().SignedUpAsync("deny-audit");
        var code = await NewCodeAsync();
        Assert.Equal(HttpStatusCode.NoContent,
            (await user.PostAsync("/api/device/deny", new { userCode = code.UserCode, workspaceId = user.WorkspaceId })).StatusCode);
        await using var db = api.Db();
        var audit = Assert.Single(db.AuditEvents.Where(a => a.Action == "agent.device_denied" && a.ActorUserId == user.Id));
        Assert.Equal(user.WorkspaceId, audit.WorkspaceId);
        Assert.Equal("device", audit.TargetType);
    }

    [Theory]
    [InlineData("short", "host", "macos", "arm64", "0.3.0")]
    [InlineData("machine-key-long-enough", "", "macos", "arm64", "0.3.0")]
    [InlineData("machine-key-long-enough", "host", "freebsd", "arm64", "0.3.0")]
    [InlineData("machine-key-long-enough", "host", "macos", "", "0.3.0")]
    [InlineData("machine-key-long-enough", "host", "macos", "arm64", "not-a-version")]
    public async Task A_device_code_request_is_validated(string key, string host, string os, string arch, string version)
    {
        var response = await api.CreateClient().PostAsJsonAsync("/api/device/code", new DeviceCodeRequest(key, host, os, arch, version), TestUser.Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_agent_below_the_minimum_version_must_upgrade()
    {
        var response = await api.CreateClient().PostAsJsonAsync("/api/device/code", Request("0.1.9"), TestUser.Json);
        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
        var user = await api.NewClient().SignedUpAsync("old-agent");
        var agent = await user.ConnectAgentAsync();
        agent.Http.DefaultRequestHeaders.Remove(AgentHeaders.Version);
        agent.Http.DefaultRequestHeaders.Add(AgentHeaders.Version, "0.1.0");
        Assert.Equal(HttpStatusCode.UpgradeRequired, (await agent.Http.PostAsync("/api/agent/heartbeat", null)).StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_and_a_reused_refresh_token_revokes_the_agent()
    {
        var user = await api.NewClient().SignedUpAsync("refresh");
        var agent = await user.ConnectAgentAsync();
        var http = api.CreateClient();
        var first = await http.PostAsJsonAsync("/api/agent/token/refresh", new RefreshRequest(agent.Tokens.RefreshToken), TestUser.Json);
        var rotated = await first.Content.ReadFromJsonAsync<TokenResponse>(TestUser.Json);
        Assert.NotEqual(agent.Tokens.RefreshToken, rotated!.RefreshToken);

        var reuse = await http.PostAsJsonAsync("/api/agent/token/refresh", new RefreshRequest(agent.Tokens.RefreshToken), TestUser.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        var afterReuse = await http.PostAsJsonAsync("/api/agent/token/refresh", new RefreshRequest(rotated.RefreshToken), TestUser.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.Http.PostAsync("/api/agent/heartbeat", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await http.PostAsJsonAsync("/api/agent/token/refresh", new RefreshRequest("made-up"), TestUser.Json)).StatusCode);
    }

    [Fact]
    public async Task Revoking_an_agent_stops_its_tokens_and_reconnecting_replaces_it()
    {
        var user = await api.NewClient().SignedUpAsync("revoke");
        var agent = await user.ConnectAgentAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await agent.Http.PostAsync("/api/agent/heartbeat", null)).StatusCode);
        var again = await user.ConnectAgentAsync(machineKey: agent.MachineKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.Http.PostAsync("/api/agent/heartbeat", null)).StatusCode);
        var list = await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/agents");
        Assert.Single(list.EnumerateArray(), a => a.GetProperty("status").GetString() == "active");

        var stranger = await api.NewClient().SignedUpAsync("revoke-stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/agents/{again.Tokens.AgentId}/revoke")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync($"/api/agents/{again.Tokens.AgentId}/revoke")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync($"/api/agents/{again.Tokens.AgentId}/revoke")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await again.Http.PostAsync("/api/agent/heartbeat", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.PostAsync($"/api/agents/{Guid.NewGuid()}/revoke")).StatusCode);
    }

    [Fact]
    public async Task Only_the_agents_own_user_moves_it_into_a_workspace_they_belong_to()
    {
        var user = await api.NewClient().SignedUpAsync("move");
        var agent = await user.ConnectAgentAsync();
        var created = await user.PostAsync("/api/workspaces", new { name = "Second" });
        var second = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var stranger = await api.NewClient().SignedUpAsync("move-stranger");
        Assert.Equal(HttpStatusCode.NotFound,
            (await stranger.SendAsync(HttpMethod.Patch, $"/api/agents/{agent.Tokens.AgentId}", new { workspaceId = stranger.WorkspaceId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await user.SendAsync(HttpMethod.Patch, $"/api/agents/{agent.Tokens.AgentId}", new { workspaceId = stranger.WorkspaceId })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await user.SendAsync(HttpMethod.Patch, $"/api/agents/{agent.Tokens.AgentId}", new { workspaceId = second })).StatusCode);
        Assert.Equal(1, (await user.GetJsonAsync($"/api/workspaces/{second}/agents")).GetArrayLength());
        Assert.Equal(0, (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/agents")).GetArrayLength());
    }
}
