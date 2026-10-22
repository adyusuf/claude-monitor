using System.Net.Http.Json;
using Podium.Contracts;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>Two machines of one user in one workspace, set up for asks (ADR-0008 §3f): the asker, and a target that reports the ask capability, has a set role and opted in.</summary>
public sealed record AskTeam(TestUser User, TestAgent Asker, TestAgent Target)
{
    public Guid WorkspaceId => User.WorkspaceId;
    public Guid AskerId => Asker.Tokens.AgentId;
    public Guid TargetId => Target.Tokens.AgentId;
}

public static class AskKit
{
    public static async Task<AskTeam> TeamAsync(ApiFactory api, bool optIn = true, string? environment = "test", string? function = "application",
        IReadOnlyList<string>? capabilities = null, string? claudeKind = ClaudeKinds.Native)
    {
        var user = await api.NewClient().SignedUpAsync("ask-user");
        (await user.SendAsync(HttpMethod.Put, $"/api/workspaces/{user.WorkspaceId}/remote-settings", new { remoteRunsEnabled = true })).EnsureSuccessStatusCode();
        var asker = await user.ConnectAgentAsync();
        var target = await user.ConnectAgentAsync(user.WorkspaceId);
        await ProfileAsync(asker, [AgentCapabilities.Ask]);
        await ProfileAsync(target, capabilities ?? [AgentCapabilities.Ask], claudeKind);
        if (environment is not null || function is not null)
        {
            (await user.SendAsync(HttpMethod.Put, $"/api/agents/{target.Tokens.AgentId}/role", new { environment, function })).EnsureSuccessStatusCode();
            (await user.SendAsync(HttpMethod.Put, $"/api/agents/{asker.Tokens.AgentId}/role", new { environment = "test", function = "development" })).EnsureSuccessStatusCode();
        }

        if (optIn) await AcceptAsync(user, target.Tokens.AgentId, true);
        return new AskTeam(user, asker, target);
    }

    public static async Task ProfileAsync(TestAgent agent, IReadOnlyList<string> capabilities, string? claudeKind = ClaudeKinds.Native) =>
        (await agent.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(ExecLevels.Off, false, null, null, 2, Claude: new ClaudePresence(claudeKind ?? ClaudeKinds.None, null, null, 1, 1, DateTimeOffset.UtcNow),
            Capabilities: capabilities), TestUser.Json)).EnsureSuccessStatusCode();

    public static async Task AcceptAsync(TestUser user, Guid agentId, bool accept) =>
        (await user.SendAsync(HttpMethod.Put, $"/api/agents/{agentId}/asks", new { accept })).EnsureSuccessStatusCode();

    public static AskCreate Ask(Guid target, string? key = null, string question = "Which branch is the build on?", string? session = null) =>
        new(key ?? RemoteKit.NewKey(), target, question, session);

    public static Task<HttpResponseMessage> PostAsync(TestAgent agent, AskCreate req) => agent.Http.PostAsJsonAsync("/api/agent/asks", req, TestUser.Json);

    public static async Task<AskCreated> CreateAsync(TestAgent agent, AskCreate req)
    {
        var response = await PostAsync(agent, req);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AskCreated>(TestUser.Json))!;
    }

    public static Task<HttpResponseMessage> StatusAsync(TestAgent target, Guid id, AskStatusUpdate update) =>
        target.Http.PostAsJsonAsync($"/api/agent/asks/{id}/status", update, TestUser.Json);

    public static Task<HttpResponseMessage> AnswerAsync(TestAgent target, Guid id, string text, string? session = "s-target") =>
        target.Http.PostAsJsonAsync($"/api/agent/asks/{id}/answer", new AskAnswer(text, session), TestUser.Json);

    public static async Task<AskCreated> DeliveredAsync(AskTeam team, string? key = null)
    {
        var created = await CreateAsync(team.Asker, Ask(team.TargetId, key));
        (await StatusAsync(team.Target, created.Id, new AskStatusUpdate(AskStatuses.Delivered, SessionExternalId: "s-target"))).EnsureSuccessStatusCode();
        return created;
    }
}
