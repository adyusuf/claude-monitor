using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>What Claude Code has on a machine (ADR-0011 §7a, step E): stored from the profile, checked again, and shown to the owner and admins only.</summary>
[Collection(ApiGroup.Name)]
public sealed class ClaudeInventoryTests(ApiFactory api)
{
    private static ClaudeInventory Inventory(IReadOnlyList<InventorySession>? sessions = null, IReadOnlyList<InventoryPlugin>? plugins = null, IReadOnlyList<string>? skills = null,
        IReadOnlyList<InventorySetting>? settings = null, ClaudeLimits? limits = null) => new(sessions, plugins, skills, settings, DateTimeOffset.UtcNow, limits);

    private static async Task SendAsync(RemoteTeam team, ClaudeInventory? inventory) =>
        (await team.Target.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(ExecLevels.Argv, false, null, null, 2, Inventory: inventory), TestUser.Json)).EnsureSuccessStatusCode();

    private static Task<HttpResponseMessage> Share(RemoteTeam team, bool share, TestUser? by = null, string? code = null) =>
        (by ?? team.Owner).SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/inventory", new { share, code });

    private static async Task<RemoteTeam> SharedTeamAsync(ApiFactory api)
    {
        var team = await RemoteKit.TeamAsync(api);
        (await Share(team, true)).EnsureSuccessStatusCode();
        return team;
    }

    private static async Task<JsonElement> DetailAsync(TestUser reader, RemoteTeam team) => await reader.GetJsonAsync($"/api/agents/{team.TargetId}/detail");

    [Fact]
    public async Task The_owner_and_admins_see_the_inventory_and_another_member_does_not()
    {
        var team = await SharedTeamAsync(api);
        await SendAsync(team, Inventory([new InventorySession("fix login", "git:host/shop", "interactive", "idle", DateTimeOffset.UtcNow)],
            [new InventoryPlugin("a@m", "1", true, "user")], ["review"], [new InventorySetting("git:host/shop", "B", "sonnet", new Dictionary<string, bool> { ["push.prod"] = false })]));

        foreach (var reader in new[] { team.Owner, team.Admin })
        {
            var inventory = (await DetailAsync(reader, team)).GetProperty("inventory");
            Assert.Equal("fix login", inventory.GetProperty("sessions")[0].GetProperty("name").GetString());
            Assert.Equal("a@m", inventory.GetProperty("plugins")[0].GetProperty("id").GetString());
            Assert.Equal("review", inventory.GetProperty("skills")[0].GetString());
            Assert.False(inventory.GetProperty("settings")[0].GetProperty("gates").GetProperty("push.prod").GetBoolean());
        }

        var other = await DetailAsync(team.Other, team);
        Assert.Equal(JsonValueKind.Null, other.GetProperty("inventory").ValueKind);
    }

    [Fact]
    public async Task What_the_machine_sends_is_checked_again_ids_shapes_modes_gates_control_characters_and_caps()
    {
        var team = await SharedTeamAsync(api);
        await SendAsync(team, Inventory(
            [new InventorySession("na\u0007me", "git:host/shop", "Bad Kind", "exploding", null), .. Enumerable.Range(0, 60).Select(i => new InventorySession($"s{i}", null, null, "idle", null))],
            [new InventoryPlugin("ok@m", "1", true, null), new InventoryPlugin("has space@m", "1", true, null), .. Enumerable.Range(0, 150).Select(i => new InventoryPlugin($"p{i}@m", null, false, null))],
            ["good-skill", "bad skill", "good-skill", .. Enumerable.Range(0, 400).Select(i => $"k{i}")],
            [new InventorySetting("git:host/shop", "Z", "Sonnet; rm -rf /", new Dictionary<string, bool> { ["Bad Name"] = true, ["merge.dev"] = true }),
             new InventorySetting("\u0007", "B", null, null)]));

        var inventory = (await DetailAsync(team.Owner, team)).GetProperty("inventory");

        var sessions = inventory.GetProperty("sessions");
        Assert.Equal(InventoryLimits.SessionsMax, sessions.GetArrayLength());
        Assert.Equal(("name", JsonValueKind.Null, JsonValueKind.Null), (sessions[0].GetProperty("name").GetString(), sessions[0].GetProperty("kind").ValueKind, sessions[0].GetProperty("status").ValueKind));
        var plugins = inventory.GetProperty("plugins");
        Assert.Equal(InventoryLimits.PluginsMax, plugins.GetArrayLength());
        Assert.DoesNotContain(plugins.EnumerateArray(), p => p.GetProperty("id").GetString()!.Contains(' '));
        var skills = inventory.GetProperty("skills").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(InventoryLimits.SkillsMax, skills.Count);
        Assert.Equal(1, skills.Count(s => s == "good-skill"));
        Assert.DoesNotContain("bad skill", skills);
        var settings = Assert.Single(inventory.GetProperty("settings").EnumerateArray());
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (settings.GetProperty("mode").ValueKind, settings.GetProperty("modeModel").ValueKind));
        Assert.Equal(["merge.dev"], settings.GetProperty("gates").EnumerateObject().Select(g => g.Name));
    }

    [Fact]
    public async Task A_later_profile_without_the_inventory_removes_it_and_an_unknown_list_stays_unknown()
    {
        var team = await SharedTeamAsync(api);
        await SendAsync(team, Inventory(plugins: [new InventoryPlugin("a@m", "1", true, null)]));
        var first = (await DetailAsync(team.Owner, team)).GetProperty("inventory");
        Assert.Equal(JsonValueKind.Null, first.GetProperty("sessions").ValueKind); // not reported: unknown, not "none"
        Assert.Equal(1, first.GetProperty("plugins").GetArrayLength());

        await SendAsync(team, null);

        Assert.Equal(JsonValueKind.Null, (await DetailAsync(team.Owner, team)).GetProperty("inventory").ValueKind);
        await using var db = api.Db();
        var row = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId);
        Assert.Null(row.ClaudeInventory);
        Assert.Null(row.ClaudeInventoryAt);
    }

    [Fact]
    public async Task The_switch_is_off_by_default_so_nothing_is_stored_or_shown_and_the_agent_is_told_so()
    {
        var team = await RemoteKit.TeamAsync(api);
        Assert.False((await team.Target.Http.GetFromJsonAsync<AgentSettings>("/api/agent/settings", TestUser.Json))!.ShareInventory);

        await SendAsync(team, Inventory(plugins: [new InventoryPlugin("a@m", "1", true, null)]));

        var detail = await DetailAsync(team.Owner, team);
        Assert.Equal((JsonValueKind.Null, false), (detail.GetProperty("inventory").ValueKind, detail.GetProperty("inventoryShared").GetBoolean()));
        await using var db = api.Db();
        Assert.Null((await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId)).ClaudeInventory);
    }

    [Fact]
    public async Task Turning_it_on_tells_the_agent_and_is_audited_and_turning_it_off_drops_what_was_stored_at_once()
    {
        var team = await SharedTeamAsync(api);
        Assert.True((await team.Target.Http.GetFromJsonAsync<AgentSettings>("/api/agent/settings", TestUser.Json))!.ShareInventory);
        await SendAsync(team, Inventory(plugins: [new InventoryPlugin("a@m", "1", true, null)]));
        Assert.Equal(JsonValueKind.Object, (await DetailAsync(team.Owner, team)).GetProperty("inventory").ValueKind);

        (await Share(team, false)).EnsureSuccessStatusCode();

        var detail = await DetailAsync(team.Owner, team);
        Assert.Equal((JsonValueKind.Null, false), (detail.GetProperty("inventory").ValueKind, detail.GetProperty("inventoryShared").GetBoolean()));
        await using var db = api.Db();
        var row = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId);
        Assert.Equal((null, null, false), (row.ClaudeInventory, row.ClaudeInventoryAt, row.ShareInventory));
        var audit = await db.AuditEvents.AsNoTracking().Where(a => a.Action == ClaudeMonitor.Api.Security.AuditActions.AgentInventoryChanged && a.TargetId == team.TargetId.ToString()).OrderBy(a => a.Id).ToListAsync();
        Assert.Equal([true, false], audit.Select(a => a.Detail!.RootElement.GetProperty("after").GetBoolean()));
    }

    [Fact]
    public async Task Only_the_machines_owner_may_switch_it_and_turning_it_on_needs_a_recent_sign_in()
    {
        var team = await RemoteKit.TeamAsync(api);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await Share(team, true, team.Admin)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await Share(team, true, team.Other)).StatusCode);

        api.Clock.Advance(TimeSpan.FromMinutes(11)); // past the re-authentication window
        await RemoteKit.AssertProblemAsync(await Share(team, true), System.Net.HttpStatusCode.Forbidden, ClaudeMonitor.Api.Remote.RemoteChecks.ReauthRequired);

        await using var db = api.Db();
        Assert.False((await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId)).ShareInventory);
    }

    [Fact]
    public async Task A_machine_moved_to_another_workspace_forgets_the_switch_and_the_inventory()
    {
        var team = await SharedTeamAsync(api);
        await SendAsync(team, Inventory(plugins: [new InventoryPlugin("a@m", "1", true, null)]));
        var second = (await (await team.Owner.PostAsync("/api/workspaces", new { name = "Second" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await team.Owner.SendAsync(HttpMethod.Patch, $"/api/agents/{team.TargetId}", new { workspaceId = second })).StatusCode);

        await using var db = api.Db();
        var row = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId);
        Assert.Equal((false, null), (row.ShareInventory, row.ClaudeInventory));
    }

    [Fact]
    public async Task The_usage_limits_are_stored_checked_and_shown_to_the_owner_and_admins_with_the_time_they_were_seen()
    {
        var team = await SharedTeamAsync(api);
        var now = api.Clock.GetUtcNow();
        await SendAsync(team, Inventory(limits: new ClaudeLimits(new ClaudeLimitWindow(23.46, now.AddHours(2)), new ClaudeLimitWindow(41.2, now.AddDays(3)), null, now.AddMinutes(-3))));

        foreach (var reader in new[] { team.Owner, team.Admin })
        {
            var limits = (await DetailAsync(reader, team)).GetProperty("inventory").GetProperty("limits");
            Assert.Equal(23.5, limits.GetProperty("fiveHour").GetProperty("usedPercentage").GetDouble());
            Assert.Equal(41.2, limits.GetProperty("sevenDay").GetProperty("usedPercentage").GetDouble());
            Assert.Equal(JsonValueKind.Null, limits.GetProperty("spend").ValueKind);
            Assert.True(limits.GetProperty("seenAt").GetDateTimeOffset() < now);
        }
    }

    [Fact]
    public async Task Limits_from_the_machine_are_clamped_and_a_reset_time_or_seen_time_that_cannot_be_right_is_dropped()
    {
        var team = await SharedTeamAsync(api);
        var now = api.Clock.GetUtcNow();
        await SendAsync(team, Inventory(limits: new ClaudeLimits(new ClaudeLimitWindow(-4, now.AddYears(5)), new ClaudeLimitWindow(99999, now.AddDays(-30)), null, now.AddDays(2))));

        var limits = (await DetailAsync(team.Owner, team)).GetProperty("inventory").GetProperty("limits");

        Assert.Equal((0, JsonValueKind.Null), (limits.GetProperty("fiveHour").GetProperty("usedPercentage").GetDouble(), limits.GetProperty("fiveHour").GetProperty("resetsAt").ValueKind));
        Assert.Equal((1000, JsonValueKind.Null), (limits.GetProperty("sevenDay").GetProperty("usedPercentage").GetDouble(), limits.GetProperty("sevenDay").GetProperty("resetsAt").ValueKind));
        Assert.True(limits.GetProperty("seenAt").GetDateTimeOffset() <= api.Clock.GetUtcNow().AddMinutes(1)); // a time in the future is replaced by now
    }

    [Fact]
    public async Task Limits_with_no_usable_window_are_not_stored_at_all()
    {
        var team = await SharedTeamAsync(api);
        await SendAsync(team, Inventory(plugins: [new InventoryPlugin("a@m", "1", true, null)], limits: new ClaudeLimits(null, null, null, DateTimeOffset.UtcNow)));

        var inventory = (await DetailAsync(team.Owner, team)).GetProperty("inventory");

        Assert.Equal(JsonValueKind.Null, inventory.GetProperty("limits").ValueKind);
    }
}
