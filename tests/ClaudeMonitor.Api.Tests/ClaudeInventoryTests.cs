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
        IReadOnlyList<InventorySetting>? settings = null) => new(sessions, plugins, skills, settings, DateTimeOffset.UtcNow);

    private static async Task SendAsync(RemoteTeam team, ClaudeInventory? inventory) =>
        (await team.Target.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(ExecLevels.Argv, false, null, null, 2, Inventory: inventory), TestUser.Json)).EnsureSuccessStatusCode();

    private static async Task<JsonElement> DetailAsync(TestUser reader, RemoteTeam team) => await reader.GetJsonAsync($"/api/agents/{team.TargetId}/detail");

    [Fact]
    public async Task The_owner_and_admins_see_the_inventory_and_another_member_does_not()
    {
        var team = await RemoteKit.TeamAsync(api);
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
        var team = await RemoteKit.TeamAsync(api);
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
        var team = await RemoteKit.TeamAsync(api);
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
}
