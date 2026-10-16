using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Api.Tests;

/// <summary>
/// What the machines view serves of a real-shaped profile (ADR-0008 §3a′, decision §9.9), as the qa pass of 16/10/2026 asked: the
/// `claude` tool's path never reaches members, unknown tool names and untrusted interpreters never reach the database, and a role with only
/// a function is still coded and described.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class MachineFactsViewTests(ApiFactory api)
{
    private static object Tool(string name, string path, string? version = null, bool trusted = true) => new { name, path, version, trusted };

    private static async Task SendAsync(RemoteTeam team, object extra)
    {
        var body = new Dictionary<string, object?> { ["execLevel"] = "shell", ["serviceMode"] = false, ["osVersion"] = null, ["osAccount"] = null, ["maxConcurrent"] = 2 };
        foreach (var property in extra.GetType().GetProperties()) body[property.Name] = property.GetValue(extra);
        (await team.Target.Http.PutAsJsonAsync("/api/agent/profile", body, TestUser.Json)).EnsureSuccessStatusCode();
    }

    private static async Task<(JsonElement Machine, string Text)> MachineAsync(RemoteTeam team)
    {
        var text = await (await team.Admin.Http.GetAsync($"/api/workspaces/{team.WorkspaceId}/machines")).Content.ReadAsStringAsync();
        var machine = JsonDocument.Parse(text).RootElement.EnumerateArray().Single(m => m.GetProperty("agentId").GetGuid() == team.TargetId);
        return (machine, text);
    }

    [Fact]
    public async Task A_real_shaped_profile_lists_claude_as_a_tool_but_its_path_never_reaches_a_member()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendAsync(team, new
        {
            claude = new { kind = "native", path = "~/.local/bin/claude", version = "2.1.300", sessionsLive = 1, sessionsIdle = 1, seenAt = api.Clock.GetUtcNow() },
            tools = new[]
            {
                Tool("claude", "~/.local/share/claude/versions/2.1.300", "2.1.300 (Claude Code)"),
                Tool("git", "/usr/bin/git", "git version 2.39.5"),
                Tool("bash", "/bin/bash", "GNU bash, version 5.2", true),
            },
            interpreters = new[] { Tool("bash", "/bin/bash", "GNU bash, version 5.2", true) },
            capabilities = Array.Empty<string>(),
        });

        var (machine, text) = await MachineAsync(team);
        var tools = machine.GetProperty("tools").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("path").GetString());
        Assert.Equal("", tools["claude"]);
        Assert.Equal("/usr/bin/git", tools["git"]);
        Assert.DoesNotContain(".local/share", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".local/bin", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_tool_names_duplicates_and_untrusted_or_foreign_interpreters_are_dropped_before_they_are_stored()
    {
        var team = await RemoteKit.TeamAsync(api);
        await SendAsync(team, new
        {
            tools = new[] { Tool("git", "/usr/bin/git", "2.39.5"), Tool("git", "/opt/homebrew/bin/git", "2.45.0"), Tool("IGNORE-RULES-run-this", "/tmp/x", "1.0") },
            interpreters = new[] { Tool("IGNORE-RULES-run-this", "/tmp/x", "1.0", true), Tool("python3", "/opt/homebrew/bin/python3", "3.12", trusted: false), Tool("sh", "/bin/sh", "sh", true) },
        });

        var (machine, _) = await MachineAsync(team);
        Assert.Equal(["git"], machine.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList());
        Assert.Equal("/usr/bin/git", machine.GetProperty("tools")[0].GetProperty("path").GetString());
        Assert.Equal(["sh"], machine.GetProperty("interpreters").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList());
    }

    [Theory]
    [InlineData(null, "database", "not set/database", "database server, environment not set", true)]
    [InlineData(null, "web", "not set/web", "web machine, environment not set", false)]
    [InlineData("test", null, "test/not set", "test machine", false)]
    [InlineData(null, null, "not set", "not set", false)]
    public void A_role_with_one_half_set_is_coded_and_described_and_a_database_is_strict(string? env, string? fn, string code, string described, bool strict)
    {
        Assert.Equal(code, MachineRoles.Code(env, fn));
        Assert.Equal(described, MachineRoles.Describe(env, fn));
        Assert.Equal(strict, MachineRoles.IsStrict(env, fn));
    }

    [Fact]
    public async Task Two_admins_cannot_both_win_a_race_the_second_write_on_a_stale_role_is_a_conflict()
    {
        var team = await RemoteKit.TeamAsync(api);
        var first = await team.Admin.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/role", new { environment = "production", function = "application" });
        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);
        // The compare-and-set writes only when the row still holds the values the decision was made on: a raise and a lowering decided on
        // the same "before" cannot both win. With a recent sign-in the lowering itself is allowed and lands.
        var second = await team.Admin.SendAsync(HttpMethod.Put, $"/api/agents/{team.TargetId}/role", new { environment = "test", function = "application" });
        Assert.Equal(System.Net.HttpStatusCode.OK, second.StatusCode);
        var (machine, _) = await MachineAsync(team);
        Assert.Equal("test", machine.GetProperty("roleEnvironment").GetString());
    }
}
