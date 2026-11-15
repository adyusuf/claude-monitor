using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Podium.Api.Data;
using Podium.Contracts;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>The export reads a big account in chunks and does not ask for each session's tasks and usage one by one (F-183).</summary>
[Collection(ApiGroup.Name)]
public sealed class PrivacyExportChunkTests(ApiFactory api)
{
    private sealed class Counter : DbCommandInterceptor
    {
        public int Tasks;
        public int Usage;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM session_tasks", StringComparison.Ordinal)) Interlocked.Increment(ref Tasks);
            if (command.CommandText.Contains("FROM session_usage", StringComparison.Ordinal)) Interlocked.Increment(ref Usage);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private async Task<(TestUser User, Guid AgentId)> SeedAsync(string tag, int sessions, int commands, int audit)
    {
        var user = await api.NewClient().SignedUpAsync(tag);
        await user.ConnectAgentAsync();
        await using var db = api.Db();
        var agentId = await db.Agents.Where(a => a.UserId == user.Id).Select(a => a.Id).SingleAsync();
        var start = api.Clock.GetUtcNow();
        var ids = new List<Guid>();
        for (var i = 0; i < sessions; i++)
        {
            var session = new HarnessSession
            {
                WorkspaceId = user.WorkspaceId,
                AgentId = agentId,
                HarnessKind = HarnessKinds.ClaudeCode,
                ExternalId = $"{tag}-{i}",
                Title = $"session {i}",
                StartedAt = start.AddSeconds(i),
                LastEventAt = start.AddSeconds(i),
            };
            ids.Add(session.Id);
            db.HarnessSessions.Add(session);
            db.SessionTasks.Add(new SessionTask { SessionId = session.Id, ExternalId = "t", Subject = $"task of {i}", CreatedAt = start, UpdatedAt = start });
            db.SessionUsage.Add(new SessionUsage { SessionId = session.Id, Model = $"model-{i}", InputTokens = i, UpdatedAt = start });
        }

        await db.SaveChangesAsync();
        for (var i = 0; i < commands; i++)
        {
            db.SessionCommands.Add(new SessionCommand
            {
                WorkspaceId = user.WorkspaceId, SessionId = ids[0], AgentId = agentId, Kind = "prompt", Body = $"command {i}",
                CreatedBy = user.Id, CreatedAt = start, ExpiresAt = start.AddHours(1),
            });
        }

        for (var i = 0; i < audit; i++) db.AuditEvents.Add(new AuditEvent { ActorUserId = user.Id, Action = "test.filler", At = start });
        await db.SaveChangesAsync();
        return (user, agentId);
    }

    private async Task<JsonElement> ExportAsync(TestUser user, Counter? counter = null)
    {
        var client = user;
        await using var host = counter is null ? null : HostKit.WithInterceptor(api, counter);
        if (host is not null) client = await HostKit.SignInAsync(api, host, user);
        var response = await client.Http.GetAsync("/api/me/export");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task The_export_reads_the_tasks_and_usage_of_all_sessions_in_one_query_each_and_keeps_each_with_its_session()
    {
        var (user, _) = await SeedAsync("export-n1", sessions: 6, commands: 0, audit: 0);
        var counter = new Counter();

        var export = await ExportAsync(user, counter);

        Assert.Equal(1, counter.Tasks);
        Assert.Equal(1, counter.Usage);
        var sessions = export.GetProperty("sessions").EnumerateArray().ToList();
        Assert.Equal(6, sessions.Count);
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal($"session {i}", sessions[i].GetProperty("title").GetString());
            Assert.Equal($"task of {i}", Assert.Single(sessions[i].GetProperty("tasks").EnumerateArray()).GetProperty("subject").GetString());
            Assert.Equal($"model-{i}", Assert.Single(sessions[i].GetProperty("usage").EnumerateArray()).GetProperty("model").GetString());
        }
    }

    [Fact]
    public async Task A_export_larger_than_one_chunk_holds_every_row_once_in_order()
    {
        var (user, _) = await SeedAsync("export-chunks", sessions: 105, commands: 505, audit: 505);

        var export = await ExportAsync(user);

        var sessions = export.GetProperty("sessions").EnumerateArray().Select(s => s.GetProperty("title").GetString()).ToList();
        Assert.Equal(Enumerable.Range(0, 105).Select(i => $"session {i}"), sessions);
        var commands = export.GetProperty("commandsSent").EnumerateArray().Select(c => c.GetProperty("body").GetString()).ToList();
        Assert.Equal(505, commands.Count);
        Assert.Equal(505, commands.Distinct().Count());
        await using var db = api.Db();
        Assert.Equal(await db.AuditEvents.CountAsync(a => a.ActorUserId == user.Id), export.GetProperty("auditTrail").GetArrayLength());
    }
}
