using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>What an agent's profile says about its role claim, Claude and tools (ADR-0008 §3a, §3a′, §3e), as the machines view serves it.</summary>
[Collection(ApiGroup.Name)]
public sealed class MachineFactsTests(ApiFactory api)
{
    private static object Tool(string name, string path, string? version = null, bool trusted = true) => new { name, path, version, trusted };

    private static async Task SendAsync(RemoteTeam team, object extra)
    {
        var body = new Dictionary<string, object?> { ["execLevel"] = "argv", ["serviceMode"] = false, ["osVersion"] = null, ["osAccount"] = null, ["maxConcurrent"] = 2 };
        foreach (var property in extra.GetType().GetProperties()) body[property.Name] = property.GetValue(extra);
        (await team.Target.Http.PutAsJsonAsync("/api/agent/profile", body, TestUser.Json)).EnsureSuccessStatusCode();
    }

    private static async Task<string> MachinesTextAsync(RemoteTeam team) =>
        await (await team.Admin.Http.GetAsync($"/api/workspaces/{team.WorkspaceId}/machines")).Content.ReadAsStringAsync();

    private static async Task<JsonElement> MachineAsync(RemoteTeam team) =>
        (await team.Admin.GetJsonAsync($"/api/workspaces/{team.WorkspaceId}/machines")).EnumerateArray()
            .Single(m => m.GetProperty("agentId").GetGuid() == team.TargetId);

    private static bool IsNull(JsonElement e, string name) => !e.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null;

    [Fact]
    public async Task An_older_agents_five_field_profile_leaves_the_new_facts_unknown()
    {
        var team = await RemoteKit.TeamAsync(api);
        var old = new { execLevel = "shell", serviceMode = true, osVersion = "14.5", osAccount = "svc", maxConcurrent = 3 };
        Assert.True((await team.Target.Http.PutAsJsonAsync("/api/agent/profile", old, TestUser.Json)).IsSuccessStatusCode);

        var machine = await MachineAsync(team);
        Assert.Equal("shell", machine.GetProperty("execLevel").GetString());
        foreach (var name in new[] { "claudeKind", "claudeVersion", "claudeSessionsIdle", "tools", "interpreters", "capabilities", "presenceAt", "claimedEnvironment", "roleEnvironment" })
        {
            Assert.True(IsNull(machine, name), name);
        }
    }

    [Fact]
    public async Task A_new_profile_without_the_facts_resets_what_an_earlier_one_reported()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendAsync(team, new
        {
            claimedEnvironment = "production",
            capabilities = new[] { "script" },
            tools = new[] { Tool("git", "/usr/bin/git") },
            claude = new { kind = "npm", path = "/opt/claude", version = "2.0.0", sessionsLive = 1, sessionsIdle = 0, seenAt = api.Clock.GetUtcNow() },
        });
        Assert.Equal("npm", (await MachineAsync(team)).GetProperty("claudeKind").GetString());

        await RemoteKit.ProfileAsync(team.Target, "argv");
        var machine = await MachineAsync(team);
        foreach (var name in new[] { "claudeKind", "claudeVersion", "claudeSessionsIdle", "tools", "capabilities", "claimedEnvironment", "presenceAt" })
        {
            Assert.True(IsNull(machine, name), name);
        }
    }

    [Fact]
    public async Task A_claim_of_production_and_database_tightens_the_effective_role_and_is_never_the_set_role()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendAsync(team, new { claimedEnvironment = "production", claimedFunction = "database" });

        var machine = await MachineAsync(team);
        Assert.Equal(("production", "database", "production", "database"), (machine.GetProperty("claimedEnvironment").GetString(),
            machine.GetProperty("claimedFunction").GetString(), machine.GetProperty("effectiveEnvironment").GetString(), machine.GetProperty("effectiveFunction").GetString()));
        Assert.True(IsNull(machine, "roleEnvironment") && IsNull(machine, "roleFunction"));
        var view = await team.Admin.GetJsonAsync($"/api/agents/{team.TargetId}/role");
        Assert.True(IsNull(view, "environment") && IsNull(view, "setAt"));
        Assert.Equal("production", view.GetProperty("effectiveEnvironment").GetString());
    }

    [Fact]
    public async Task A_claim_of_test_fills_nothing_and_loosens_nothing()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendAsync(team, new { claimedEnvironment = "test", claimedFunction = "application" });
        var unset = await MachineAsync(team);
        Assert.Equal(("test", "application"), (unset.GetProperty("claimedEnvironment").GetString(), unset.GetProperty("claimedFunction").GetString()));
        Assert.True(IsNull(unset, "effectiveEnvironment") && IsNull(unset, "effectiveFunction"));

        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/role", new { environment = "production", function = "database" })).EnsureSuccessStatusCode();
        var set = await MachineAsync(team);
        Assert.Equal(("production", "database"), (set.GetProperty("effectiveEnvironment").GetString(), set.GetProperty("effectiveFunction").GetString()));
    }

    [Fact]
    public async Task A_claim_outside_the_closed_sets_is_dropped_and_not_stored()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendAsync(team, new { claimedEnvironment = "production", claimedFunction = "database" });
        await SendAsync(team, new { claimedEnvironment = "prod", claimedFunction = "mainframe" });

        Assert.True(IsNull(await MachineAsync(team), "claimedEnvironment") && IsNull(await MachineAsync(team), "claimedFunction"));
        await using var db = api.Db();
        var row = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId);
        Assert.Equal((null, null), (row.ClaimedEnvironment, row.ClaimedFunction));
    }

    [Fact]
    public async Task Claude_presence_is_clamped_stripped_and_kept_off_the_machines_view_except_its_kind_version_and_idle_count()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendAsync(team, new
        {
            claude = new { kind = "native", path = "~/.local/bin/claude", version = "2.1.300", sessionsLive = 2, sessionsIdle = 5, seenAt = api.Clock.GetUtcNow() },
        });

        var machine = await MachineAsync(team);
        Assert.Equal(("native", "2.1.300", 2), (machine.GetProperty("claudeKind").GetString(), machine.GetProperty("claudeVersion").GetString(),
            machine.GetProperty("claudeSessionsIdle").GetInt32()));
        Assert.Equal(api.Clock.GetUtcNow(), machine.GetProperty("presenceAt").GetDateTimeOffset(), TimeSpan.FromSeconds(1));
        Assert.DoesNotContain(".local/bin", await MachinesTextAsync(team), StringComparison.Ordinal);
        await using var db = api.Db();
        var row = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId);
        Assert.Equal(("~/.local/bin/claude", 2, 2), (row.ClaudePath, row.ClaudeSessionsLive, row.ClaudeSessionsIdle));
    }

    [Fact]
    public async Task An_unknown_claude_kind_and_negative_counts_are_not_trusted()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendAsync(team, new { claude = new { kind = "snap", path = "/x", version = "1", sessionsLive = 1, sessionsIdle = 0, seenAt = api.Clock.GetUtcNow() } });
        Assert.True(IsNull(await MachineAsync(team), "claudeKind"));

        await SendAsync(team, new { claude = new { kind = "none", path = (string?)null, version = (string?)null, sessionsLive = -4, sessionsIdle = -2, seenAt = api.Clock.GetUtcNow() } });
        var machine = await MachineAsync(team);
        Assert.Equal(("none", 0), (machine.GetProperty("claudeKind").GetString(), machine.GetProperty("claudeSessionsIdle").GetInt32()));
    }

    [Fact]
    public async Task Tools_are_capped_at_the_limit_and_their_text_is_cleaned()
    {
        var team = await RemoteKit.TeamAsync(api);
        var tools = Enumerable.Range(0, 40).Select(i => Tool("git", $"/usr/bin/git{i}", i == 0 ? "git version\u0000 2.\n4‮5​" : $"v{i}")).ToList();
        tools.Insert(0, Tool("", "/usr/bin/none"));
        await SendAsync(team, new { tools, interpreters = new[] { Tool("sh", "/bin/sh", "5.2") } });

        var machine = await MachineAsync(team);
        var stored = machine.GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(MachineFacts.ToolsMax, stored.Count);
        Assert.Equal("git version 2.45", stored[0].GetProperty("version").GetString());
        Assert.Equal("/usr/bin/git31", stored[^1].GetProperty("path").GetString());
        var interpreter = Assert.Single(machine.GetProperty("interpreters").EnumerateArray());
        Assert.Equal(("sh", "5.2", true), (interpreter.GetProperty("name").GetString(), interpreter.GetProperty("version").GetString(), interpreter.GetProperty("trusted").GetBoolean()));
        Assert.NotEqual(JsonValueKind.Null, machine.GetProperty("presenceAt").ValueKind);
    }

    [Fact]
    public async Task Only_the_known_capabilities_are_kept_once_each()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendAsync(team, new { capabilities = new[] { "script", "root", "ask", "script", "" } });
        var capabilities = (await MachineAsync(team)).GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Equal(["script", "ask"], capabilities);
    }
}
