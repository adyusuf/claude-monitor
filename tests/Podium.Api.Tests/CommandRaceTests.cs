using System.Net;
using System.Net.Http.Json;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// A command's and a permission request's status move by compare-and-set: of two requests that start together exactly one wins, and the
/// other is told 409 (two tabs answering one request; the owner cancelling while the agent reports the command delivered).
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class CommandRaceTests(ApiFactory api)
{
    private const int Rounds = 6;

    /// <summary>Starts every action at the same moment and returns what each did; nothing begins before all are ready.</summary>
    private static async Task<T[]> TogetherAsync<T>(params Func<Task<T>>[] actions)
    {
        var start = new TaskCompletionSource();
        var running = actions.Select(a => Task.Run(async () =>
        {
            await start.Task;
            return await a();
        })).ToArray();
        start.SetResult();
        return await Task.WhenAll(running);
    }

    [Fact]
    public async Task Two_simultaneous_answers_to_one_permission_request_give_one_answer_and_one_409()
    {
        var team = await SessionKit.TeamAsync(api);
        for (var round = 0; round < Rounds; round++)
        {
            var request = await SessionKit.AskPermissionAsync(team);
            var answers = await TogetherAsync(
                () => team.Owner.PostAsync($"/api/permission-requests/{request}/answer", new { decision = "allow" }),
                () => team.Owner.PostAsync($"/api/permission-requests/{request}/answer", new { decision = "deny", reason = "no" }));

            Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Conflict], answers.Select(a => a.StatusCode).Order());
            await using var db = api.Db();
            Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.Action == AuditActions.PermissionAnswered && a.TargetId == request.ToString()));
            var stored = await db.PermissionRequests.AsNoTracking().SingleAsync(p => p.Id == request);
            var winner = answers[0].StatusCode == HttpStatusCode.NoContent ? PermissionDecisions.Allow : PermissionDecisions.Deny;
            Assert.Equal(winner, stored.Decision);
        }
    }

    [Fact]
    public async Task A_cancel_racing_the_agents_delivered_report_lets_exactly_one_of_them_win()
    {
        var team = await SessionKit.TeamAsync(api);
        for (var round = 0; round < Rounds; round++)
        {
            var command = await SessionKit.QueueStopAsync(team);
            var both = await TogetherAsync(
                () => team.Owner.PostAsync($"/api/commands/{command}/cancel"),
                () => team.Agent.Http.PostAsJsonAsync($"/api/agent/commands/{command}/status", new CommandStatusUpdate(CommandStatuses.Delivered, null), TestUser.Json));

            Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Conflict], both.Select(r => r.StatusCode).Order());
            var expected = both[0].StatusCode == HttpStatusCode.NoContent ? CommandStatuses.Cancelled : CommandStatuses.Delivered;
            Assert.Equal(expected, await SessionKit.CommandStatusAsync(team.Admin, team.SessionId, command));
        }
    }

    [Fact]
    public async Task A_command_the_agent_already_reported_delivered_cannot_be_cancelled()
    {
        var team = await SessionKit.TeamAsync(api);
        var command = await SessionKit.QueueStopAsync(team);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Agent.Http.PostAsJsonAsync($"/api/agent/commands/{command}/status",
            new CommandStatusUpdate(CommandStatuses.Delivered, null), TestUser.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await team.Owner.PostAsync($"/api/commands/{command}/cancel")).StatusCode);
        Assert.Equal(CommandStatuses.Delivered, await SessionKit.CommandStatusAsync(team.Admin, team.SessionId, command));
    }
}
