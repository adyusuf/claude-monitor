using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>What a person types about a machine (ADR-0008 §3a′): tags and the note are an admin's, listening services the owner's choice.</summary>
[Collection(ApiGroup.Name)]
public sealed class MachineTagsTests(ApiFactory api)
{
    private static Task<HttpResponseMessage> PutTagsAsync(TestUser user, Guid agent, Dictionary<string, string>? tags) =>
        user.SendAsync(HttpMethod.Put, $"/api/agents/{agent}/tags", new { tags });

    private static Task<HttpResponseMessage> PutNoteAsync(TestUser user, Guid agent, string? note) =>
        user.SendAsync(HttpMethod.Put, $"/api/agents/{agent}/note", new { note });

    private static Task<HttpResponseMessage> PutServicesAsync(TestUser user, Guid agent, bool share) =>
        user.SendAsync(HttpMethod.Put, $"/api/agents/{agent}/services", new { share });

    private static async Task<JsonElement> MachineAsync(TestUser reader, RemoteTeam team) =>
        (await reader.GetJsonAsync($"/api/workspaces/{team.WorkspaceId}/machines")).EnumerateArray()
            .Single(m => m.GetProperty("agentId").GetGuid() == team.TargetId);

    [Theory]
    [InlineData("a", true)]
    [InlineData("A-b_c.1", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("a=b", false)]
    [InlineData("\u00fcn\u00ef", false)]
    public void A_tag_part_is_one_to_thirty_two_safe_ascii_characters(string text, bool expected) =>
        Assert.Equal(expected, MachineTags.IsPart(text));

    [Fact]
    public void Tags_are_valid_up_to_ten_pairs_and_the_note_refuses_control_characters_but_not_line_breaks()
    {
        Assert.True(MachineTags.IsValid(null));
        Assert.True(MachineTags.IsValid(Enumerable.Range(0, 10).ToDictionary(i => $"k{i}", i => $"v{i}")));
        Assert.False(MachineTags.IsValid(Enumerable.Range(0, 11).ToDictionary(i => $"k{i}", i => $"v{i}")));
        Assert.False(MachineTags.IsValid(new Dictionary<string, string> { ["ok"] = "no way" }));
        Assert.False(MachineTags.IsValid(new Dictionary<string, string> { [new string('k', 33)] = "v" }));
        Assert.True(MachineTags.IsNote("line one\nline two\ttabbed"));
        Assert.False(MachineTags.IsNote("bell\a"));
        Assert.False(MachineTags.IsNote(new string('x', MachineTags.NoteMax + 1)));
        Assert.True(MachineTags.IsNote(new string('x', MachineTags.NoteMax)));
    }

    [Fact]
    public async Task An_admin_sets_tags_and_every_member_reads_them_in_the_machine_list_and_the_change_is_audited()
    {
        var team = await RemoteKit.TeamAsync(api);
        var viewer = await RemoteKit.MemberAsync(api, team.Admin, "mt-viewer", "viewer");
        var set = await PutTagsAsync(team.Admin, team.TargetId, new() { ["project"] = "shop", ["region"] = "eu" });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        foreach (var reader in new[] { team.Admin, team.Owner, viewer })
        {
            var tags = (await MachineAsync(reader, team)).GetProperty("tags");
            Assert.Equal(("shop", "eu"), (tags.GetProperty("project").GetString(), tags.GetProperty("region").GetString()));
        }

        await using var db = api.Db();
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == "agent.tags_changed" && a.TargetId == team.TargetId.ToString());
        Assert.Equal(team.Admin.Id, audit.ActorUserId);
        Assert.Equal("shop", audit.Detail!.RootElement.GetProperty("after").GetProperty("project").GetString());
    }

    [Fact]
    public async Task An_empty_tag_set_clears_the_tags()
    {
        var team = await RemoteKit.TeamAsync(api);
        (await PutTagsAsync(team.Admin, team.TargetId, new() { ["a"] = "b" })).EnsureSuccessStatusCode();
        (await PutTagsAsync(team.Admin, team.TargetId, new())).EnsureSuccessStatusCode();
        var machine = await MachineAsync(team.Admin, team);
        Assert.True(!machine.TryGetProperty("tags", out var tags) || tags.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Too_many_tags_or_an_unsafe_character_is_a_bad_request_with_the_tag_code_and_changes_nothing()
    {
        var team = await RemoteKit.TeamAsync(api);
        (await PutTagsAsync(team.Admin, team.TargetId, new() { ["keep"] = "me" })).EnsureSuccessStatusCode();
        var tooMany = Enumerable.Range(0, 11).ToDictionary(i => $"k{i}", i => $"v{i}");
        foreach (var bad in new[] { tooMany, new Dictionary<string, string> { ["a"] = "x y" }, new Dictionary<string, string> { ["a=b"] = "x" } })
        {
            var response = await PutTagsAsync(team.Admin, team.TargetId, bad);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("invalid_tag", await response.Content.ReadAsStringAsync());
        }

        Assert.Equal("me", (await MachineAsync(team.Admin, team)).GetProperty("tags").GetProperty("keep").GetString());
    }

    [Fact]
    public async Task Only_an_admin_sets_tags_or_the_note_anyone_else_gets_not_found()
    {
        var team = await RemoteKit.TeamAsync(api);
        var viewer = await RemoteKit.MemberAsync(api, team.Admin, "mt-viewer", "viewer");
        var outsider = await api.NewClient().SignedUpAsync("mt-outsider");
        foreach (var user in new[] { team.Owner, team.Other, viewer, outsider })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await PutTagsAsync(user, team.TargetId, new() { ["a"] = "b" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await PutNoteAsync(user, team.TargetId, "restart with care")).StatusCode);
        }

        await using var db = api.Db();
        Assert.False(await db.AuditEvents.AnyAsync(a => (a.Action == "agent.tags_changed" || a.Action == "agent.note_changed") && a.TargetId == team.TargetId.ToString()));
    }

    [Fact]
    public async Task The_note_is_stored_and_audited_by_length_only_and_a_control_character_is_refused()
    {
        var team = await RemoteKit.TeamAsync(api);
        const string note = "Restart order: db first.\nNever on Fridays.";
        Assert.Equal(HttpStatusCode.OK, (await PutNoteAsync(team.Admin, team.TargetId, note)).StatusCode);
        var bad = await PutNoteAsync(team.Admin, team.TargetId, "no\abells");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("invalid_note", await bad.Content.ReadAsStringAsync());

        await using var db = api.Db();
        var stored = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId);
        Assert.Equal(note, stored.AdminNote);
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == "agent.note_changed" && a.TargetId == team.TargetId.ToString());
        Assert.Equal(note.Length, audit.Detail!.RootElement.GetProperty("afterLength").GetInt32());
        Assert.DoesNotContain("Restart order", audit.Detail.RootElement.GetRawText());
    }

    [Fact]
    public async Task The_note_never_reaches_the_machine_list()
    {
        var team = await RemoteKit.TeamAsync(api);
        (await PutNoteAsync(team.Admin, team.TargetId, "secret runbook text")).EnsureSuccessStatusCode();
        var text = await (await team.Admin.Http.GetAsync($"/api/workspaces/{team.WorkspaceId}/machines")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret runbook text", text);
    }

    [Fact]
    public async Task Only_the_machines_owner_decides_about_services_and_turning_them_off_drops_what_was_stored()
    {
        var team = await RemoteKit.TeamAsync(api);
        Assert.Equal(HttpStatusCode.NotFound, (await PutServicesAsync(team.Admin, team.TargetId, true)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutServicesAsync(team.Other, team.TargetId, true)).StatusCode);

        var on = await PutServicesAsync(team.Owner, team.TargetId, true);
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        var profile = new AgentProfile(ExecLevels.Argv, false, null, null, 2, Services: [new ServiceInfo(5432, "tcp", "postgres"), new ServiceInfo(70000, "tcp", "bad"), new ServiceInfo(22, "udp", "x")]);
        (await team.Target.Http.PutAsJsonAsync("/api/agent/profile", profile, TestUser.Json)).EnsureSuccessStatusCode();
        await using (var db = api.Db())
        {
            var row = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId);
            Assert.True(row.ShareServices);
            var services = JsonSerializer.Deserialize<JsonElement>(row.Services!);
            Assert.Equal(1, services.GetArrayLength());
            Assert.Equal(5432, services[0].GetProperty("port").GetInt32());
        }

        Assert.Equal(HttpStatusCode.OK, (await PutServicesAsync(team.Owner, team.TargetId, false)).StatusCode);
        await using var after = api.Db();
        var off = await after.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId);
        Assert.False(off.ShareServices);
        Assert.Null(off.Services);
    }

    [Fact]
    public async Task Services_reported_while_the_owner_has_not_opted_in_are_not_stored()
    {
        var team = await RemoteKit.TeamAsync(api);
        var profile = new AgentProfile(ExecLevels.Argv, false, null, null, 2, Services: [new ServiceInfo(5432, "tcp", "postgres")]);
        (await team.Target.Http.PutAsJsonAsync("/api/agent/profile", profile, TestUser.Json)).EnsureSuccessStatusCode();
        await using var db = api.Db();
        Assert.Null((await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId)).Services);
    }
}
