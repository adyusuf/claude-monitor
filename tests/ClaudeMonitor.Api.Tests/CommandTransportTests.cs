using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Tests;

/// <summary>How a command reached its session is stored with "applied" and shown on the command (ADR-0007 section 9); an older agent says nothing and a strange value is dropped.</summary>
[Collection(ApiGroup.Name)]
public sealed class CommandTransportTests(ApiFactory api)
{
    private async Task<(TestUser Owner, TestAgent Agent, Guid SessionId, Guid CommandId)> NewCommandAsync()
    {
        var owner = await api.NewClient().SignedUpAsync("transport-owner");
        var agent = await owner.ConnectAgentAsync();
        await agent.SendAsync(TestAgent.Hook("sess-" + Guid.NewGuid(), "SessionStart", new { }, api.Clock.GetUtcNow()));
        var sessionId = (await owner.GetJsonAsync($"/api/workspaces/{owner.WorkspaceId}/sessions")).GetProperty("items")[0].GetProperty("id").GetGuid();
        var created = await owner.PostAsync($"/api/sessions/{sessionId}/commands", new { kind = "prompt", body = "Run the tests" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (owner, agent, sessionId, (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    private static Task<HttpResponseMessage> ReportAsync(TestAgent agent, Guid id, string status, string? transport) =>
        agent.Http.PostAsJsonAsync($"/api/agent/commands/{id}/status", new CommandStatusUpdate(status, null, null, transport), TestUser.Json);

    private static async Task<JsonElement> RowAsync(TestUser owner, Guid sessionId) => (await owner.GetJsonAsync($"/api/sessions/{sessionId}/commands"))[0];

    [Theory]
    [InlineData(CommandTransports.Hook)]
    [InlineData(CommandTransports.Inbox)]
    [InlineData(CommandTransports.Channel)]
    public async Task An_applied_command_shows_the_way_it_reached_the_session(string transport)
    {
        var (owner, agent, sessionId, id) = await NewCommandAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(agent, id, CommandStatuses.Applied, transport)).StatusCode);
        var row = await RowAsync(owner, sessionId);
        Assert.Equal((CommandStatuses.Applied, transport), (row.GetProperty("status").GetString(), row.GetProperty("transport").GetString()));
    }

    [Fact]
    public async Task A_command_not_applied_yet_and_one_an_older_agent_applied_have_no_transport()
    {
        var (owner, agent, sessionId, id) = await NewCommandAsync();
        Assert.Equal(JsonValueKind.Null, (await RowAsync(owner, sessionId)).GetProperty("transport").ValueKind);
        Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(agent, id, CommandStatuses.Delivered, CommandTransports.Hook)).StatusCode); // not the report that says how
        Assert.Equal(JsonValueKind.Null, (await RowAsync(owner, sessionId)).GetProperty("transport").ValueKind);
        Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(agent, id, CommandStatuses.Applied, null)).StatusCode);
        var row = await RowAsync(owner, sessionId);
        Assert.Equal(CommandStatuses.Applied, row.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("transport").ValueKind);
    }

    [Theory]
    [InlineData("carrier-pigeon")]
    [InlineData("")]
    [InlineData("HOOK")]
    public async Task A_way_the_api_does_not_know_is_dropped_and_the_report_still_counts(string transport)
    {
        var (owner, agent, sessionId, id) = await NewCommandAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(agent, id, CommandStatuses.Applied, transport)).StatusCode);
        var row = await RowAsync(owner, sessionId);
        Assert.Equal(CommandStatuses.Applied, row.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("transport").ValueKind);
    }

    [Fact]
    public async Task A_report_of_an_old_agent_without_the_field_is_still_understood()
    {
        var (owner, agent, sessionId, id) = await NewCommandAsync();
        var response = await agent.Http.PostAsJsonAsync($"/api/agent/commands/{id}/status", new { status = "applied", result = (string?)null }, TestUser.Json);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(CommandStatuses.Applied, (await RowAsync(owner, sessionId)).GetProperty("status").GetString());
    }

    // ---- delivered with a transport: written into the session, not shown yet --------------------------------------------

    [Theory]
    [InlineData(CommandTransports.Inbox)]
    [InlineData(CommandTransports.Channel)]
    public async Task A_command_pushed_into_the_session_shows_the_transport_while_it_is_still_delivered(string transport)
    {
        var (owner, agent, sessionId, id) = await NewCommandAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(agent, id, CommandStatuses.Delivered, null)).StatusCode); // the stream hands it over
        Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(agent, id, CommandStatuses.Delivered, transport)).StatusCode);
        var row = await RowAsync(owner, sessionId);
        Assert.Equal((CommandStatuses.Delivered, transport), (row.GetProperty("status").GetString(), row.GetProperty("transport").GetString()));
    }

    [Fact]
    public async Task A_push_taken_back_clears_the_transport_and_the_hook_that_then_applies_it_sets_its_own()
    {
        var (owner, agent, sessionId, id) = await NewCommandAsync();
        await ReportAsync(agent, id, CommandStatuses.Delivered, CommandTransports.Inbox);
        Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(agent, id, CommandStatuses.Delivered, "")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await RowAsync(owner, sessionId)).GetProperty("transport").ValueKind);
        await ReportAsync(agent, id, CommandStatuses.Applied, CommandTransports.Hook);
        Assert.Equal(CommandTransports.Hook, (await RowAsync(owner, sessionId)).GetProperty("transport").GetString());
    }

    [Fact]
    public async Task A_late_push_report_never_takes_an_applied_command_back_and_the_first_delivery_time_stands()
    {
        var (owner, agent, sessionId, id) = await NewCommandAsync();
        await ReportAsync(agent, id, CommandStatuses.Delivered, null);
        var first = (await RowAsync(owner, sessionId)).GetProperty("deliveredAt").GetString();
        api.Clock.Advance(TimeSpan.FromSeconds(30));
        await ReportAsync(agent, id, CommandStatuses.Delivered, CommandTransports.Channel);
        Assert.Equal(first, (await RowAsync(owner, sessionId)).GetProperty("deliveredAt").GetString());

        await ReportAsync(agent, id, CommandStatuses.Applied, CommandTransports.Channel);
        Assert.Equal(HttpStatusCode.Conflict, (await ReportAsync(agent, id, CommandStatuses.Delivered, "")).StatusCode);
        var row = await RowAsync(owner, sessionId);
        Assert.Equal((CommandStatuses.Applied, CommandTransports.Channel), (row.GetProperty("status").GetString(), row.GetProperty("transport").GetString()));
    }

    [Fact]
    public async Task A_delivered_report_with_a_strange_transport_keeps_what_was_there()
    {
        var (owner, agent, sessionId, id) = await NewCommandAsync();
        await ReportAsync(agent, id, CommandStatuses.Delivered, CommandTransports.Inbox);
        Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(agent, id, CommandStatuses.Delivered, "carrier-pigeon")).StatusCode);
        Assert.Equal(CommandTransports.Inbox, (await RowAsync(owner, sessionId)).GetProperty("transport").GetString());
    }

    // ---- the push transport a machine has on, told with the profile ------------------------------------------------------

    private static Task<HttpResponseMessage> ProfileAsync(TestAgent agent, string? push) =>
        agent.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(ExecLevels.Off, false, null, null, 2, PushTransport: push), TestUser.Json);

    [Theory]
    [InlineData(CommandTransports.Inbox, CommandTransports.Inbox)]
    [InlineData(CommandTransports.Channel, CommandTransports.Channel)]
    [InlineData("carrier-pigeon", null)]
    [InlineData(null, null)]
    public async Task The_session_says_which_push_transport_its_machine_has_on(string? reported, string? expected)
    {
        var (owner, agent, sessionId, _) = await NewCommandAsync();
        (await ProfileAsync(agent, reported)).EnsureSuccessStatusCode();
        var session = (await owner.GetJsonAsync($"/api/sessions/{sessionId}")).GetProperty("session");
        Assert.Equal(expected, session.GetProperty("pushTransport").ValueKind == JsonValueKind.Null ? null : session.GetProperty("pushTransport").GetString());
    }

    [Fact]
    public async Task Turning_push_off_is_heard_with_the_next_profile()
    {
        var (owner, agent, sessionId, _) = await NewCommandAsync();
        await ProfileAsync(agent, CommandTransports.Inbox);
        await ProfileAsync(agent, null);
        Assert.Equal(JsonValueKind.Null, (await owner.GetJsonAsync($"/api/sessions/{sessionId}")).GetProperty("session").GetProperty("pushTransport").ValueKind);
    }
}
