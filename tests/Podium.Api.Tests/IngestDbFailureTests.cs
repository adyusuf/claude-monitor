using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Ingest;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Podium.Api.Tests;

/// <summary>
/// F-240: what the per-event checks let through and PostgreSQL still refuses as data (SQLSTATE class 22) answers the batch with a 422, not a 500;
/// nothing of the batch is stored (not even its number, so a retry is taken again) and the log names no content. A database that is down is
/// still a server error.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class IngestDbFailureTests(ApiFactory api)
{
    private const string Content = "prompt-text-that-must-not-reach-the-log";

    private static HttpClient AgentOn(WebApplicationFactory<Program> host, TestAgent agent)
    {
        var http = host.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", agent.Tokens.AccessToken);
        http.DefaultRequestHeaders.Add(AgentHeaders.Version, "0.3.0");
        return http;
    }

    private async Task<(TestUser User, TestAgent Agent, EventBatch Batch)> BatchAsync(string tag)
    {
        var user = await api.NewClient().SignedUpAsync(tag);
        var agent = await user.ConnectAgentAsync();
        var batch = new EventBatch(4_000_001, [TestAgent.Hook("sess-" + Guid.NewGuid(), "UserPromptSubmit", new { prompt = Content }, api.Clock.GetUtcNow().AddMinutes(-1))]);
        return (user, agent, batch);
    }

    [Fact]
    public async Task A_data_error_from_postgresql_answers_422_stores_nothing_and_logs_no_content()
    {
        var (user, agent, batch) = await BatchAsync("ingest-22");
        var fault = new CommandFault("INSERT INTO session_events") { Error = new PostgresException("invalid byte sequence", "ERROR", "ERROR", PostgresErrorCodes.CharacterNotInRepertoire) };
        var log = new TracingLog();
        await using var host = HostKit.WithInterceptor(api, fault, log);

        var response = await AgentOn(host, agent).PostAsJsonAsync("/api/agent/batches", batch, TestUser.Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(BatchIngestor.Unstorable, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Contains(log.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Category == typeof(BatchIngestor).FullName);
        Assert.All(log.Entries, e => Assert.DoesNotContain(Content, e.Everything, StringComparison.Ordinal));
        await using var db = api.Db();
        Assert.Equal(0, await db.HarnessSessions.CountAsync(s => s.WorkspaceId == user.WorkspaceId));
        Assert.Equal(0, await db.AgentBatches.CountAsync(b => b.BatchSeq == batch.BatchSeq && db.Agents.Any(a => a.Id == b.AgentId && a.WorkspaceId == user.WorkspaceId)));

        // The same batch number is taken again once the database accepts it.
        var retry = await agent.Http.PostAsJsonAsync("/api/agent/batches", batch, TestUser.Json);
        Assert.Equal(new BatchAck(batch.BatchSeq, false, 1), await retry.Content.ReadFromJsonAsync<BatchAck>(TestUser.Json));
    }

    [Fact]
    public async Task A_database_that_is_down_is_still_a_server_error()
    {
        var (_, agent, batch) = await BatchAsync("ingest-down");
        var fault = new CommandFault("INSERT INTO session_events") { Error = new DatabaseDownException() };
        await using var host = HostKit.WithInterceptor(api, fault);

        var response = await AgentOn(host, agent).PostAsJsonAsync("/api/agent/batches", batch, TestUser.Json);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
