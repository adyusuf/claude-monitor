using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Security;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The optional first prompt of a Claude start from the web (ADR-0014 P2): checked, kept only until the agent takes it, never listed or audited.</summary>
[Collection(ApiGroup.Name)]
public sealed class ClaudeControlPromptTests(ApiFactory api)
{
    private const string Project = "git:host/shop";
    private const string Secret = "please look at the failing login test";

    private async Task<AskTeam> TeamAsync()
    {
        var team = await AskKit.TeamAsync(api, optIn: false, environment: "dev", function: "development", claudeKind: ClaudeKinds.Native);
        (await team.User.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/claude", new { accept = true })).EnsureSuccessStatusCode();
        return team;
    }

    private static Task<HttpResponseMessage> Start(AskTeam team, string? prompt, string kind = AgentActionKinds.ClaudeStart, string? sessionRef = null) =>
        team.User.PostAsync($"/api/agents/{team.TargetId}/actions", new AgentActionRequest(kind, null, kind == AgentActionKinds.ClaudeStart ? Project : null, null, sessionRef, prompt));

    [Fact]
    public async Task A_prompt_is_stored_trimmed_sent_to_the_agent_but_never_listed_or_audited()
    {
        var team = await TeamAsync();
        var created = await (await Start(team, "  " + Secret + "\nand then fix it  ")).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json);

        var row = await ActionKit.RowAsync(api, created!.Id);
        Assert.Equal(Secret + "\nand then fix it", row.Prompt);
        await using (var db = api.Db())
        {
            var waiting = await AgentActions.WaitingForAsync(db, team.TargetId, api.Clock.GetUtcNow(), CancellationToken.None);
            Assert.Equal(Secret + "\nand then fix it", Assert.Single(waiting).Prompt);
            var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == AuditActions.AgentActionRequested && a.TargetId == team.TargetId.ToString());
            Assert.True(audit.Detail!.RootElement.GetProperty("hasPrompt").GetBoolean());
            Assert.DoesNotContain("failing login", audit.Detail.RootElement.GetRawText(), StringComparison.Ordinal);
        }

        var listed = await team.User.GetJsonAsync($"/api/agents/{team.TargetId}/actions");
        Assert.DoesNotContain("failing login", listed.GetRawText(), StringComparison.Ordinal);
        Assert.False(listed[0].TryGetProperty("prompt", out _));
    }

    [Fact]
    public async Task The_prompt_is_dropped_when_the_agent_takes_the_action()
    {
        var team = await TeamAsync();
        var created = await (await Start(team, Secret)).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json);

        Assert.Equal(HttpStatusCode.NoContent, (await ActionKit.ReportAsync(team.Target, created!.Id, AgentActionStatuses.Delivered)).StatusCode);

        Assert.Null((await ActionKit.RowAsync(api, created.Id)).Prompt);
    }

    [Fact]
    public async Task The_prompt_is_dropped_when_the_action_expires_unread()
    {
        var team = await TeamAsync();
        var created = await (await Start(team, Secret)).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json);

        api.Clock.Advance(AgentActionKinds.QueueTtl + TimeSpan.FromSeconds(1));
        await team.User.GetJsonAsync($"/api/agents/{team.TargetId}/actions");

        var row = await ActionKit.RowAsync(api, created!.Id);
        Assert.Equal((AgentActionStatuses.Expired, null), (row.Status, row.Prompt));
    }

    [Fact]
    public async Task Turning_the_switch_off_drops_a_queued_prompt()
    {
        var team = await TeamAsync();
        var created = await (await Start(team, Secret)).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json);

        (await team.User.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/claude", new { accept = false })).EnsureSuccessStatusCode();

        Assert.Null((await ActionKit.RowAsync(api, created!.Id)).Prompt);
    }

    [Theory]
    [InlineData("bad\u0000text")]
    [InlineData("bell\u0007")]
    public async Task A_prompt_with_control_characters_is_refused_but_new_lines_and_tabs_are_fine(string prompt)
    {
        var team = await TeamAsync();
        var refused = await Start(team, prompt);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(AgentActionErrors.InvalidPrompt, (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("prompt")[0].GetString());
        Assert.Empty(await ActionKit.RowsAsync(api, team.TargetId));
        Assert.Equal(HttpStatusCode.OK, (await Start(team, "line one\n\tline two")).StatusCode);
    }

    [Fact]
    public async Task A_prompt_over_the_limit_is_refused_and_one_at_the_limit_is_taken()
    {
        var team = await TeamAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await Start(team, new string('p', AgentActionKinds.PromptMax + 1))).StatusCode);
        Assert.Empty(await ActionKit.RowsAsync(api, team.TargetId));
        Assert.Equal(HttpStatusCode.OK, (await Start(team, new string('p', AgentActionKinds.PromptMax))).StatusCode);
    }

    [Fact]
    public async Task A_blank_prompt_is_no_prompt_and_a_stop_takes_none()
    {
        var team = await TeamAsync();
        var created = await (await Start(team, "   \n ")).Content.ReadFromJsonAsync<AgentActionView>(TestUser.Json);
        Assert.Null((await ActionKit.RowAsync(api, created!.Id)).Prompt);

        Assert.Equal(HttpStatusCode.BadRequest, (await Start(team, Secret, AgentActionKinds.ClaudeStop, "34a6ea9d")).StatusCode);
    }
}
