using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// F-188: <c>permission_requests.client_key</c>, the agent's own key for an ask, unique per agent where it is set (as runs carry <c>ClientKey</c>),
/// so a create sent again after a lost response can find the request it made. Requests without a key (older agents) stay unlimited.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class PermissionRequestKeyMigrationTests(ApiFactory api)
{
    private const string Before = "20261116070419_IntegrationAccountUnique";
    private const string Migration = "20261116070708_PermissionRequestKey";
    private const string Index = "ix_permission_requests_agent_id_client_key";

    [Fact]
    public async Task The_column_and_the_partial_unique_index_are_added_and_a_roll_back_drops_them()
    {
        await using var m = await MigrationDb.CreateAsync(api, Before);

        await m.MigrateAsync(Migration);

        Assert.Equal("YES", await m.ScalarAsync("SELECT is_nullable FROM information_schema.columns WHERE table_name = 'permission_requests' AND column_name = 'client_key'"));
        Assert.Equal(
            $"CREATE UNIQUE INDEX {Index} ON public.permission_requests USING btree (agent_id, client_key) WHERE (client_key IS NOT NULL)",
            await m.IndexAsync(Index));

        await m.MigrateAsync(Before);
        Assert.Equal(0, await m.ColumnCountAsync("permission_requests", "client_key"));
        Assert.Null(await m.IndexAsync(Index));
    }

    [Fact]
    public async Task One_key_holds_one_request_per_agent_while_requests_without_a_key_and_other_agents_are_free()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "client-key");
        var first = (await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", new { command = "make build" })).Id;
        var second = (await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", new { command = "make test" })).Id;
        await using var db = api.Db();
        Assert.Equal(2, await db.PermissionRequests.CountAsync(p => (p.Id == first || p.Id == second) && p.ClientKey == null));
        var agent = (await db.PermissionRequests.AsNoTracking().FirstAsync(p => p.Id == first)).AgentId;
        await db.Database.ExecuteSqlAsync($"UPDATE permission_requests SET client_key = 'local-1' WHERE id = {first}");

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => CopyAsync(db, first, agent: null));
        await CopyAsync(db, first, agent: Guid.NewGuid());

        Assert.Contains(Index, refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, await db.PermissionRequests.CountAsync(p => p.AgentId == agent && p.ClientKey == "local-1"));
    }

    /// <summary>A copy of a request under a new id, on the same agent or on <paramref name="agent"/>.</summary>
    private static Task<int> CopyAsync(Podium.Api.Data.PodiumDb db, Guid source, Guid? agent)
    {
        var changes = agent is null ? $$"""{"id": "{{Guid.NewGuid()}}"}""" : $$"""{"id": "{{Guid.NewGuid()}}", "agent_id": "{{agent}}"}""";
        return db.Database.ExecuteSqlAsync($"""
            INSERT INTO permission_requests
            SELECT (jsonb_populate_record(NULL::permission_requests, to_jsonb(p) || {changes}::jsonb)).* FROM permission_requests p WHERE p.id = {source}
            """);
    }
}
