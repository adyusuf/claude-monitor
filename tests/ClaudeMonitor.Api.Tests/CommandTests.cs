using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Tests;

[Collection(ApiGroup.Name)]
public sealed class CommandTests(ApiFactory api)
{
    private async Task<(TestUser Owner, TestUser Viewer, TestAgent Agent, Guid SessionId, string External)> SetupAsync()
    {
        var owner = await api.NewClient().SignedUpAsync("cmd-owner");
        var viewer = await api.NewClient().SignedUpAsync("cmd-viewer");
        (await owner.PostAsync($"/api/workspaces/{owner.WorkspaceId}/invitations", new { email = viewer.Email, role = "admin" })).EnsureSuccessStatusCode();
        (await viewer.PostAsync("/api/invitations/accept", new { token = api.Mail.TokenFor(viewer.Email) })).EnsureSuccessStatusCode();
        var agent = await owner.ConnectAgentAsync();
        var external = "sess-" + Guid.NewGuid();
        await agent.SendAsync(TestAgent.Hook(external, "SessionStart", new { }, api.Clock.GetUtcNow()));
        var id = (await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/sessions")).GetProperty("items")[0].GetProperty("id").GetGuid();
        return (owner, viewer, agent, id, external);
    }

    /// <summary>Reads the SSE stream until an event with the given name arrives, and returns its data.</summary>
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

    [Fact]
    public async Task The_owner_sends_a_prompt_the_agent_receives_and_reports_it()
    {
        var (owner, viewer, agent, id, external) = await SetupAsync();
        var created = await owner.PostAsync($"/api/sessions/{id}/commands", new { kind = "prompt", body = "Run the tests" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var commandId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var stream = await OpenAsync(agent.Http, "/api/agent/stream");
        var queued = await NextEventAsync(stream, AgentStreamEvents.Command);
        Assert.Equal(commandId, queued.GetProperty("id").GetGuid());
        Assert.Equal(external, queued.GetProperty("sessionExternalId").GetString());
        Assert.Equal("Run the tests", queued.GetProperty("body").GetString());

        var live = await owner.PostAsync($"/api/sessions/{id}/commands", new { kind = "stop" });
        var liveId = (await live.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(liveId, (await NextEventAsync(stream, AgentStreamEvents.Command)).GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.NoContent, (await agent.Http.PostAsJsonAsync($"/api/agent/commands/{commandId}/status", new CommandStatusUpdate("delivered", null), TestUser.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await agent.Http.PostAsJsonAsync($"/api/agent/commands/{commandId}/status", new CommandStatusUpdate("applied", "ok"), TestUser.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await agent.Http.PostAsJsonAsync($"/api/agent/commands/{commandId}/status", new CommandStatusUpdate("failed", null), TestUser.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await agent.Http.PostAsJsonAsync($"/api/agent/commands/{commandId}/status", new CommandStatusUpdate("queued", null), TestUser.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.Http.PostAsJsonAsync($"/api/agent/commands/{Guid.NewGuid()}/status", new CommandStatusUpdate("applied", null), TestUser.Json)).StatusCode);

        var list = await viewer.GetJsonAsync($"/api/sessions/{id}/commands");
        Assert.Contains(list.EnumerateArray(), c => c.GetProperty("id").GetGuid() == commandId && c.GetProperty("status").GetString() == "applied");
    }

    [Fact]
    public async Task Only_the_owner_commands_even_an_admin_cannot()
    {
        var (owner, admin, _, id, _) = await SetupAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync($"/api/sessions/{id}/commands", new { kind = "prompt", body = "x" })).StatusCode);
        Assert.False((await admin.GetJsonAsync($"/api/sessions/{id}")).GetProperty("canCommand").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync($"/api/sessions/{id}/commands", new { kind = "rm -rf" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync($"/api/sessions/{id}/commands", new { kind = "prompt", body = " " })).StatusCode);
        var stranger = await api.NewClient().SignedUpAsync("cmd-stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/sessions/{id}/commands", new { kind = "stop" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/sessions/{id}/commands")).StatusCode);
    }

    [Fact]
    public async Task A_queued_command_can_be_cancelled_by_its_owner_only()
    {
        var (owner, admin, _, id, _) = await SetupAsync();
        var created = await owner.PostAsync($"/api/sessions/{id}/commands", new { kind = "prompt", body = "later" });
        var commandId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync($"/api/commands/{commandId}/cancel")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/commands/{commandId}/cancel")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/commands/{commandId}/cancel")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/commands/{Guid.NewGuid()}/cancel")).StatusCode);
    }

    [Fact]
    public async Task An_ended_session_takes_no_commands()
    {
        var (owner, _, agent, id, external) = await SetupAsync();
        await agent.SendAsync(TestAgent.Hook(external, "SessionEnd", new { }, api.Clock.GetUtcNow().AddSeconds(1)));
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync($"/api/sessions/{id}/commands", new { kind = "stop" })).StatusCode);
    }

    [Fact]
    public async Task A_permission_request_is_answered_from_the_web_and_reaches_the_agent()
    {
        var (owner, admin, agent, id, external) = await SetupAsync();
        using var web = await OpenAsync(admin.Http, $"/api/workspaces/{owner.WorkspaceId}/stream");
        await NextEventAsync(web, "ready");
        var create = await agent.Http.PostAsJsonAsync("/api/agent/permission-requests",
            new PermissionRequestCreate(HarnessKinds.ClaudeCode, external, "Bash", JsonSerializer.SerializeToElement(new { command = "git push" }), 120), TestUser.Json);
        var request = (await create.Content.ReadFromJsonAsync<PermissionRequestCreated>(TestUser.Json))!;
        Assert.Equal(request.Id, (await NextEventAsync(web, "permission")).GetProperty("id").GetGuid());
        var session = await owner.GetJsonAsync($"/api/sessions/{id}");
        Assert.Equal("waiting", session.GetProperty("session").GetProperty("status").GetString());
        Assert.Equal(1, session.GetProperty("session").GetProperty("openPermissions").GetInt32());
        var open = await admin.GetJsonAsync($"/api/sessions/{id}/permission-requests?status=open");
        Assert.Equal("git push", open[0].GetProperty("toolInput").GetProperty("command").GetString());

        using var stream = await OpenAsync(agent.Http, "/api/agent/stream");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync($"/api/permission-requests/{request.Id}/answer", new { decision = "allow" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync($"/api/permission-requests/{request.Id}/answer", new { decision = "maybe" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync($"/api/permission-requests/{request.Id}/answer", new { decision = "deny", reason = new string('r', 501) })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/permission-requests/{request.Id}/answer", new { decision = "deny", reason = "not now" })).StatusCode);
        var answer = await NextEventAsync(stream, AgentStreamEvents.PermissionAnswer);
        Assert.Equal("deny", answer.GetProperty("decision").GetString());
        Assert.Equal("not now", answer.GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/permission-requests/{request.Id}/answer", new { decision = "allow" })).StatusCode);

        var polled = await agent.Http.GetFromJsonAsync<JsonElement>($"/api/agent/permission-requests/{request.Id}");
        Assert.Equal("answered", polled.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await agent.Http.GetAsync($"/api/agent/permission-requests/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/permission-requests/{Guid.NewGuid()}/answer", new { decision = "allow" })).StatusCode);
    }

    [Fact]
    public async Task A_question_is_answered_with_a_chosen_option_and_nothing_else_is_accepted()
    {
        var (owner, admin, agent, _, external) = await SetupAsync();
        async Task<Guid> Ask(string tool, object input) => (await (await agent.Http.PostAsJsonAsync("/api/agent/permission-requests",
            new PermissionRequestCreate(HarnessKinds.ClaudeCode, external, tool, JsonSerializer.SerializeToElement(input), 120), TestUser.Json))
            .Content.ReadFromJsonAsync<PermissionRequestCreated>(TestUser.Json))!.Id;
        var options = new[] { new { label = "Left", description = "" }, new { label = "Right", description = "" } };
        var question = await Ask("AskUserQuestion", new { questions = new[] { new { question = "Which way?", header = "Way", multiSelect = false, options } } });
        var bash = await Ask("Bash", new { command = "ls" });
        var malformed = await Ask("AskUserQuestion", new { questions = "none" });
        using var stream = await OpenAsync(agent.Http, "/api/agent/stream");
        Task<HttpStatusCode> Answer(Guid id, object body, TestUser? as_ = null) => (as_ ?? owner).PostAsync($"/api/permission-requests/{id}/answer", body).ContinueWith(t => t.Result.StatusCode);
        var chosen = new Dictionary<string, string> { ["Which way?"] = "Right" };

        Assert.Equal(HttpStatusCode.Forbidden, await Answer(question, new { decision = "allow", answers = chosen }, admin));
        Assert.Equal(HttpStatusCode.BadRequest, await Answer(bash, new { decision = "allow", answers = chosen })); // only the question tool takes answers
        Assert.Equal(HttpStatusCode.BadRequest, await Answer(malformed, new { decision = "allow", answers = chosen }));
        Assert.Equal(HttpStatusCode.BadRequest, await Answer(question, new { decision = "deny", answers = chosen })); // a refusal chooses nothing
        Assert.Equal(HttpStatusCode.BadRequest, await Answer(question, new { decision = "allow", answers = new Dictionary<string, string>() }));
        Assert.Equal(HttpStatusCode.BadRequest, await Answer(question, new { decision = "allow", answers = new Dictionary<string, string> { ["Another?"] = "x" } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Answer(question, new { decision = "allow", answers = new Dictionary<string, string> { ["Which way?"] = " " } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Answer(question, new { decision = "allow", answers = new Dictionary<string, string> { ["Which way?"] = new string('x', 501) } }));
        Assert.Equal(HttpStatusCode.NoContent, await Answer(question, new { decision = "allow", answers = chosen }));
        var message = await NextEventAsync(stream, AgentStreamEvents.PermissionAnswer);
        Assert.Equal("allow", message.GetProperty("decision").GetString());
        Assert.Equal("Right", message.GetProperty("answers").GetProperty("Which way?").GetString());
        Assert.Equal(HttpStatusCode.Conflict, await Answer(question, new { decision = "allow", answers = chosen }));
    }

    [Fact]
    public async Task A_permission_request_needs_a_known_session_and_expires()
    {
        var (owner, _, agent, id, external) = await SetupAsync();
        var unknown = await agent.Http.PostAsJsonAsync("/api/agent/permission-requests",
            new PermissionRequestCreate(HarnessKinds.ClaudeCode, "nope", "Bash", JsonSerializer.SerializeToElement(new { }), 60), TestUser.Json);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var invalid = await agent.Http.PostAsJsonAsync("/api/agent/permission-requests",
            new PermissionRequestCreate(HarnessKinds.ClaudeCode, external, "", JsonSerializer.SerializeToElement(new { }), 60), TestUser.Json);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var created = await (await agent.Http.PostAsJsonAsync("/api/agent/permission-requests",
            new PermissionRequestCreate(HarnessKinds.ClaudeCode, external, "Edit", JsonSerializer.SerializeToElement(new { }), 5), TestUser.Json))
            .Content.ReadFromJsonAsync<PermissionRequestCreated>(TestUser.Json);
        api.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/permission-requests/{created!.Id}/answer", new { decision = "allow" })).StatusCode);
        var stranger = await api.NewClient().SignedUpAsync("perm-stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/sessions/{id}/permission-requests")).StatusCode);
    }

    [Fact]
    public async Task A_revoked_agent_is_told_on_its_stream()
    {
        var (owner, _, agent, _, _) = await SetupAsync();
        using var stream = await OpenAsync(agent.Http, "/api/agent/stream");
        await owner.PostAsync($"/api/agents/{agent.Tokens.AgentId}/revoke");
        await NextEventAsync(stream, AgentStreamEvents.Revoked);
    }
}
