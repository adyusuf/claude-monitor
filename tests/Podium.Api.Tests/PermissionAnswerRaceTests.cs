using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// Two answers to one permission request at the same moment (the approvals page, the band and the session page show it at once): exactly one
/// stands. The other is refused with 409, saves nothing (no decision, no rule, no audit row) and sends the agent nothing.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class PermissionAnswerRaceTests(ApiFactory api)
{
    /// <summary>
    /// Runs the answers while the test holds the request's row: both have read it as open and wait on it, then the row is let go. Without a
    /// compare-and-set both writes would land, the last one winning.
    /// </summary>
    private async Task<HttpResponseMessage[]> HeldTogetherAsync(Guid requestId, params Func<Task<HttpResponseMessage>>[] answers)
    {
        await using var holder = api.Db();
        await using var hold = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlAsync($"SELECT 1 FROM permission_requests WHERE id = {requestId} FOR UPDATE");
        var running = answers.Select(a => Task.Run(a)).ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await WaitingAsync() < answers.Length) await Task.Delay(20, timeout.Token);
        await hold.CommitAsync();
        return await Task.WhenAll(running);
    }

    private async Task<int> WaitingAsync()
    {
        await using var db = api.Db();
        return await db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'").SingleAsync();
    }

    /// <summary>The names of the PermissionAnswer events on the agent's stream until the command sent after them arrives.</summary>
    private static async Task<int> AnswersBeforeCommandAsync(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var answers = 0;
        string? current = null;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal)) current = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && current == AgentStreamEvents.PermissionAnswer) answers++;
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && current == AgentStreamEvents.Command) return answers;
        }

        throw new InvalidOperationException("stream ended");
    }

    private static async Task<string?> TitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();

    [Fact]
    public async Task Two_answers_at_once_give_one_answer_one_conflict_one_message_to_the_agent_and_no_rule_from_the_loser()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "par");
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", new { command = "make build" });
        var sessionId = (await w.Owner.GetJsonAsync($"/api/workspaces/{w.WorkspaceId}/sessions")).GetProperty("items")[0].GetProperty("id").GetGuid();
        var response = await w.Agent.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/agent/stream"), HttpCompletionOption.ResponseHeadersRead);
        using var stream = new StreamReader(await response.Content.ReadAsStreamAsync());

        var answers = await HeldTogetherAsync(request.Id,
            () => ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, PermissionDecisions.Allow, ApprovalDurations.FiveMinutes),
            () => ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, PermissionDecisions.Deny));

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.NoContent);
        var lost = Assert.Single(answers, a => a.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(RemoteErrors.NotPending, await TitleAsync(lost)); // the page can say why: answered meanwhile, or expired
        var allowWon = answers[0].StatusCode == HttpStatusCode.NoContent;
        var row = await ApprovalRuleKit.RequestRowAsync(api, request.Id);
        Assert.Equal((PermissionStatuses.Answered, allowWon ? PermissionDecisions.Allow : PermissionDecisions.Deny), (row.Status, row.Decision));
        Assert.Equal(allowWon ? 1 : 0, (await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId)).Count); // a deny that won leaves no rule behind
        Assert.Single(await ApprovalRuleKit.AuditAsync(api, w.WorkspaceId, AuditActions.PermissionAnswered));

        (await w.Owner.PostAsync($"/api/sessions/{sessionId}/commands", new { kind = CommandKinds.Stop })).EnsureSuccessStatusCode();
        Assert.Equal(1, await AnswersBeforeCommandAsync(stream));
    }

    [Fact]
    public async Task An_answer_held_up_until_the_request_expired_is_refused_and_saves_nothing()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "par-exp");
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", new { command = "make build" }, waitSeconds: 5);
        api.Clock.Advance(TimeSpan.FromSeconds(6));

        var answer = await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, PermissionDecisions.Allow, ApprovalDurations.FiveMinutes);

        Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
        Assert.Equal(RemoteErrors.NotPending, await TitleAsync(answer));
        Assert.Equal(PermissionStatuses.Open, (await ApprovalRuleKit.RequestRowAsync(api, request.Id)).Status);
        Assert.Empty(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId));
        Assert.Empty(await ApprovalRuleKit.AuditAsync(api, w.WorkspaceId, AuditActions.PermissionAnswered));
    }
}
