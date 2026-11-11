using System.Net.Http.Json;
using System.Text.Json;
using Podium.Contracts;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>A workspace whose admin created it, a member who runs one session on their own machine in it, and what a test needs to command that session.</summary>
public sealed record SessionTeam(TestUser Admin, TestUser Owner, TestAgent Agent, Guid SessionId, string External)
{
    public Guid WorkspaceId => Admin.WorkspaceId;
}

public static class SessionKit
{
    /// <summary>The admin creates the workspace, a member joins it, connects an agent there and starts a session (still running).</summary>
    public static async Task<SessionTeam> TeamAsync(ApiFactory api)
    {
        var admin = await api.NewClient().SignedUpAsync("sk-admin");
        var owner = await RemoteKit.MemberAsync(api, admin, "sk-owner");
        var agent = await owner.ConnectAgentAsync(admin.WorkspaceId);
        var external = "sess-" + Guid.NewGuid();
        await agent.SendAsync(TestAgent.Hook(external, "SessionStart", new { }, api.Clock.GetUtcNow()));
        var id = (await admin.GetJsonAsync($"/api/workspaces/{admin.WorkspaceId}/sessions")).GetProperty("items")[0].GetProperty("id").GetGuid();
        return new SessionTeam(admin, owner, agent, id, external);
    }

    /// <summary>The owner queues a stop command and returns its id.</summary>
    public static async Task<Guid> QueueStopAsync(SessionTeam team)
    {
        var response = await team.Owner.PostAsync($"/api/sessions/{team.SessionId}/commands", new { kind = "stop" });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>The session's agent asks for permission to run a command; returns the request id.</summary>
    public static async Task<Guid> AskPermissionAsync(SessionTeam team)
    {
        var response = await team.Agent.Http.PostAsJsonAsync("/api/agent/permission-requests",
            new PermissionRequestCreate(HarnessKinds.ClaudeCode, team.External, "Bash", JsonSerializer.SerializeToElement(new { command = "git push" }), 120), TestUser.Json);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PermissionRequestCreated>(TestUser.Json))!.Id;
    }

    public static async Task RemoveMemberAsync(TestUser admin, TestUser member) =>
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Delete, $"/api/workspaces/{admin.WorkspaceId}/members/{member.Id}")).StatusCode);

    public static async Task<string> CommandStatusAsync(TestUser reader, Guid session, Guid command) =>
        (await reader.GetJsonAsync($"/api/sessions/{session}/commands")).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == command).GetProperty("status").GetString()!;

    public static async Task<string> PermissionStatusAsync(TestUser reader, Guid session, Guid request) =>
        (await reader.GetJsonAsync($"/api/sessions/{session}/permission-requests")).EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == request).GetProperty("status").GetString()!;
}
