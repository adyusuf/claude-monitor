using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>The agents list is ordered by status, host name and newest enrolment, holds at most 500 rows and leaves out agents revoked
/// longer ago than <see cref="ApiConfig.RevokedAgentsListedFor"/> (MachineEndpoints.List).</summary>
[Collection(ApiGroup.Name)]
public sealed class AgentListOrderTests(ApiFactory api)
{
    private const int Cap = 500;

    private static Machine NewMachine(Guid workspaceId, string hostname, DateTimeOffset at) => new()
    {
        WorkspaceId = workspaceId,
        MachineKeyHash = Guid.NewGuid().ToString("N"),
        Hostname = hostname,
        Os = OsKinds.Linux,
        Arch = "x64",
        FirstSeenAt = at,
        LastSeenAt = at,
    };

    private static Agent NewAgent(Machine machine, Guid userId, string status, DateTimeOffset enrolledAt, DateTimeOffset? revokedAt = null) => new()
    {
        MachineId = machine.Id,
        UserId = userId,
        WorkspaceId = machine.WorkspaceId,
        Version = "0.3.0",
        Status = status,
        EnrolledAt = enrolledAt,
        RevokedAt = revokedAt,
    };

    private async Task SeedAsync(IEnumerable<Machine> machines, IEnumerable<Agent> agents)
    {
        await using var db = api.Db();
        db.Machines.AddRange(machines);
        db.Agents.AddRange(agents);
        await db.SaveChangesAsync();
    }

    private static async Task<List<JsonElement>> AgentsAsync(TestUser user) =>
        (await user.GetJsonAsync($"/api/workspaces/{user.WorkspaceId}/agents")).EnumerateArray().ToList();

    [Fact]
    public async Task Agents_are_listed_active_first_then_by_host_name_then_newest_enrolment_first()
    {
        var user = await api.NewClient().SignedUpAsync("al-order");
        var now = api.Clock.GetUtcNow();
        var alpha = NewMachine(user.WorkspaceId, "alpha", now);
        var bravo = NewMachine(user.WorkspaceId, "bravo", now);
        var activeBravo = NewAgent(bravo, user.Id, AgentStatuses.Active, now);
        var activeAlpha = NewAgent(alpha, user.Id, AgentStatuses.Active, now);
        var revokedOld = NewAgent(alpha, user.Id, AgentStatuses.Revoked, now.AddDays(-2));
        var revokedNew = NewAgent(alpha, user.Id, AgentStatuses.Revoked, now.AddDays(-1));
        await SeedAsync([alpha, bravo], [revokedOld, activeBravo, revokedNew, activeAlpha]);

        var ids = (await AgentsAsync(user)).Select(a => a.GetProperty("id").GetGuid()).ToList();

        Assert.Equal([activeAlpha.Id, activeBravo.Id, revokedNew.Id, revokedOld.Id], ids);
    }

    [Fact]
    public async Task The_list_holds_500_agents_and_leaves_the_rest_out()
    {
        var user = await api.NewClient().SignedUpAsync("al-cap");
        var now = api.Clock.GetUtcNow();
        var machines = Enumerable.Range(0, Cap + 1).Select(i => NewMachine(user.WorkspaceId, $"host-{i:D3}", now)).ToList();
        await SeedAsync(machines, machines.Select(m => NewAgent(m, user.Id, AgentStatuses.Active, now)));

        var rows = await AgentsAsync(user);

        Assert.Equal(Cap, rows.Count);
        Assert.Equal("host-000", rows[0].GetProperty("hostname").GetString());
        Assert.Equal("host-499", rows[^1].GetProperty("hostname").GetString());
    }

    [Fact]
    public async Task When_the_list_is_cut_the_revoked_rows_go_first()
    {
        var user = await api.NewClient().SignedUpAsync("al-cut");
        var now = api.Clock.GetUtcNow();
        var active = Enumerable.Range(0, Cap).Select(i => NewMachine(user.WorkspaceId, $"host-{i:D3}", now)).ToList();
        var revokedMachine = NewMachine(user.WorkspaceId, "aaa-first-by-name", now);
        var agents = active.Select(m => NewAgent(m, user.Id, AgentStatuses.Active, now))
            .Append(NewAgent(revokedMachine, user.Id, AgentStatuses.Revoked, now));
        await SeedAsync([.. active, revokedMachine], agents);

        var rows = await AgentsAsync(user);

        Assert.Equal(Cap, rows.Count);
        Assert.All(rows, r => Assert.Equal(AgentStatuses.Active, r.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Revoked_agents_older_than_the_window_are_left_out_and_newer_ones_stay()
    {
        var user = await api.NewClient().SignedUpAsync("al-window");
        var now = api.Clock.GetUtcNow();
        var window = api.Services.GetRequiredService<ApiConfig>().RevokedAgentsListedFor;
        Assert.Equal(TimeSpan.FromDays(30), window); // F-024: the default N
        var machine = NewMachine(user.WorkspaceId, "host", now);
        var activeOld = NewAgent(machine, user.Id, AgentStatuses.Active, now - window - TimeSpan.FromDays(10));
        var atTheEdge = NewAgent(machine, user.Id, AgentStatuses.Revoked, now - window - TimeSpan.FromDays(1), now - window);
        var justOver = NewAgent(machine, user.Id, AgentStatuses.Revoked, now - window - TimeSpan.FromDays(1), now - window - TimeSpan.FromSeconds(1));
        var recent = NewAgent(machine, user.Id, AgentStatuses.Revoked, now - TimeSpan.FromDays(2), now - TimeSpan.FromDays(1));
        await SeedAsync([machine], [activeOld, atTheEdge, justOver, recent]);

        var ids = (await AgentsAsync(user)).Select(a => a.GetProperty("id").GetGuid()).ToList();

        Assert.Equal([activeOld.Id, recent.Id, atTheEdge.Id], ids);
        await using var db = api.Db();
        Assert.NotNull(await db.Agents.FindAsync(justOver.Id)); // left out of the list, never deleted
    }
}
