using System.Net;
using System.Text.Json;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>An agent works only while its user is a member of its workspace; removing the member revokes the agent.</summary>
[Collection(ApiGroup.Name)]
public sealed class AgentMembershipTests(ApiFactory api)
{
    private async Task<(TestUser Owner, TestUser Member)> TeamAsync(string memberRole = "member")
    {
        var owner = await api.NewClient().SignedUpAsync("am-owner");
        var member = await api.NewClient().SignedUpAsync("am-member");
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = member.Email, role = memberRole }))
            .EnsureSuccessStatusCode();
        (await member.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(member.Email) })).EnsureSuccessStatusCode();
        return (owner, member);
    }

    private async Task AssertWorksAsync(TestAgent agent)
    {
        Assert.Equal(HttpStatusCode.NoContent, (await agent.Http.PostAsync("/api/agent/heartbeat", null)).StatusCode);
        Assert.True((await agent.SendAsync(TestAgent.Hook("s-" + Guid.NewGuid(), "SessionStart", new { }, api.Clock.GetUtcNow()))).IsSuccessStatusCode);
        using var stream = await OpenAsync(agent.Http, "/api/agent/stream");
        await NextEventAsync(stream, AgentStreamEvents.Ready);
    }

    private static async Task AssertRejectedAsync(TestAgent agent)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.Http.PostAsync("/api/agent/heartbeat", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await agent.SendAsync(TestAgent.Hook("s-" + Guid.NewGuid(), "SessionStart", new { }, DateTimeOffset.UtcNow))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.Http.GetAsync("/api/agent/stream")).StatusCode);
    }

    private static async Task<JsonElement> NextEventAsync(StreamReader reader, string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? current = null;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal)) current = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && current == name) return JsonDocument.Parse(line[6..]).RootElement;
        }

        throw new InvalidOperationException("stream ended");
    }

    private static async Task<StreamReader> OpenAsync(HttpClient http, string url)
    {
        var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new StreamReader(await response.Content.ReadAsStreamAsync());
    }

    private async Task AssertRevokedByAsync(TestAgent agent, Guid workspaceId, Guid actorId)
    {
        await using var db = api.Db();
        var row = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agent.Tokens.AgentId);
        Assert.Equal(AgentStatuses.Revoked, row.Status);
        Assert.NotNull(row.RevokedAt);
        Assert.Equal(actorId, row.RevokedBy);
        var tokens = await db.AgentTokens.AsNoTracking().Where(t => t.AgentId == row.Id).ToListAsync();
        Assert.NotEmpty(tokens);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
        var audit = Assert.Single(await db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == "agent.revoked" && a.TargetId == row.Id.ToString()).ToListAsync());
        Assert.Equal(workspaceId, audit.WorkspaceId);
        Assert.Equal(actorId, audit.ActorUserId);
        Assert.Equal("agent", audit.TargetType);
        Assert.Equal("member_removed", audit.Detail!.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Removing_a_member_stops_their_agent_revokes_it_and_audits_it()
    {
        var (owner, member) = await TeamAsync();
        var agent = await member.ConnectAgentAsync(owner.WorkspaceId);
        await AssertWorksAsync(agent);

        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.SendAsync(HttpMethod.Delete, $"/api/workspaces/{owner.WorkspaceId}/members/{member.Id}")).StatusCode);

        await AssertRejectedAsync(agent);
        await AssertRevokedByAsync(agent, owner.WorkspaceId, owner.Id);
    }

    [Fact]
    public async Task Removing_a_member_tells_their_open_agent_stream_it_was_revoked()
    {
        var (owner, member) = await TeamAsync();
        var agent = await member.ConnectAgentAsync(owner.WorkspaceId);
        using var stream = await OpenAsync(agent.Http, "/api/agent/stream");
        await NextEventAsync(stream, AgentStreamEvents.Ready);

        (await owner.SendAsync(HttpMethod.Delete, $"/api/workspaces/{owner.WorkspaceId}/members/{member.Id}")).EnsureSuccessStatusCode();

        await NextEventAsync(stream, AgentStreamEvents.Revoked);
    }

    [Fact]
    public async Task A_member_who_leaves_loses_their_agent_and_is_the_recorded_actor()
    {
        var (owner, member) = await TeamAsync();
        var agent = await member.ConnectAgentAsync(owner.WorkspaceId);
        using var stream = await OpenAsync(agent.Http, "/api/agent/stream");
        await NextEventAsync(stream, AgentStreamEvents.Ready);

        Assert.Equal(HttpStatusCode.NoContent,
            (await member.SendAsync(HttpMethod.Delete, $"/api/workspaces/{owner.WorkspaceId}/members/{member.Id}")).StatusCode);

        await NextEventAsync(stream, AgentStreamEvents.Revoked);
        await AssertRejectedAsync(agent);
        await AssertRevokedByAsync(agent, owner.WorkspaceId, member.Id);
    }

    [Fact]
    public async Task Removing_a_member_leaves_their_agent_in_another_workspace_and_other_members_agents_working()
    {
        var (owner, member) = await TeamAsync();
        var removedHere = await member.ConnectAgentAsync(owner.WorkspaceId);
        var sameUserElsewhere = await member.ConnectAgentAsync(member.WorkspaceId);
        var ownersAgent = await owner.ConnectAgentAsync();

        (await owner.SendAsync(HttpMethod.Delete, $"/api/workspaces/{owner.WorkspaceId}/members/{member.Id}")).EnsureSuccessStatusCode();

        await AssertRejectedAsync(removedHere);
        await AssertWorksAsync(sameUserElsewhere);
        await AssertWorksAsync(ownersAgent);
        await using var db = api.Db();
        Assert.Equal(AgentStatuses.Active, (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == sameUserElsewhere.Tokens.AgentId)).Status);
        Assert.Equal(AgentStatuses.Active, (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == ownersAgent.Tokens.AgentId)).Status);
        Assert.False(await db.AuditEvents.AnyAsync(a => a.Action == "agent.revoked"
            && (a.TargetId == sameUserElsewhere.Tokens.AgentId.ToString() || a.TargetId == ownersAgent.Tokens.AgentId.ToString())));
    }

    [Fact]
    public async Task A_viewer_cannot_remove_another_member_and_their_agent_keeps_working()
    {
        var (owner, viewer) = await TeamAsync("viewer");
        var other = await api.NewClient().SignedUpAsync("am-other");
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = other.Email, role = "member" })).EnsureSuccessStatusCode();
        (await other.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(other.Email) })).EnsureSuccessStatusCode();
        var otherAgent = await other.ConnectAgentAsync(owner.WorkspaceId);

        Assert.Equal(HttpStatusCode.NotFound,
            (await viewer.SendAsync(HttpMethod.Delete, $"/api/workspaces/{owner.WorkspaceId}/members/{other.Id}")).StatusCode);

        await AssertWorksAsync(otherAgent);
        await using var db = api.Db();
        Assert.Equal(AgentStatuses.Active, (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == otherAgent.Tokens.AgentId)).Status);
    }

    [Fact]
    public async Task An_active_agent_whose_user_is_no_longer_a_member_is_refused_by_authentication_alone()
    {
        var (owner, member) = await TeamAsync();
        var agent = await member.ConnectAgentAsync(owner.WorkspaceId);
        await AssertWorksAsync(agent);
        await using (var db = api.Db())
        {
            await db.WorkspaceMembers.Where(m => m.WorkspaceId == owner.WorkspaceId && m.UserId == member.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.RemovedAt, api.Clock.GetUtcNow()));
            Assert.Equal(AgentStatuses.Active, (await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agent.Tokens.AgentId)).Status);
        }

        await AssertRejectedAsync(agent);
    }

    [Fact]
    public async Task An_agent_of_an_archived_workspace_is_refused()
    {
        var (owner, member) = await TeamAsync();
        var agent = await member.ConnectAgentAsync(owner.WorkspaceId);
        var elsewhere = await member.ConnectAgentAsync(member.WorkspaceId);
        await using (var db = api.Db())
        {
            await db.Workspaces.Where(w => w.Id == owner.WorkspaceId)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, "archived"));
        }

        await AssertRejectedAsync(agent);
        await AssertWorksAsync(elsewhere);
    }
}
