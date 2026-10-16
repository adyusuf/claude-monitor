using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Who sets the role of a machine and how (ADR-0008 §3a): admins only, audited, and lowering needs a recent sign-in.</summary>
[Collection(ApiGroup.Name)]
public sealed class MachineRoleTests(ApiFactory api)
{
    private static object Role(string? environment, string? function, string? code = null) => new { environment, function, code };

    private static Task<HttpResponseMessage> PutAsync(TestUser user, Guid agent, string? environment, string? function, string? code = null) =>
        user.SendAsync(HttpMethod.Put, $"/api/agents/{agent}/role", Role(environment, function, code));

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static bool IsNull(JsonElement e, string name) => !e.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null;

    private static async Task<JsonElement> MachineAsync(TestUser reader, RemoteTeam team) =>
        (await reader.GetJsonAsync($"/api/workspaces/{team.WorkspaceId}/machines")).EnumerateArray()
            .Single(m => m.GetProperty("agentId").GetGuid() == team.TargetId);

    private async Task<TestUser> SignedInAgainAsync(TestUser user)
    {
        var again = api.NewClient();
        (await again.PostAsync("/api/auth/login", new { email = user.Email, password = TestUser.Password })).EnsureSuccessStatusCode();
        return again;
    }

    [Fact]
    public async Task An_admin_sets_the_role_and_members_of_any_rank_read_it_in_the_view_and_in_the_machine_list()
    {
        var team = await RemoteKit.TeamAsync(api);
        var viewer = await RemoteKit.MemberAsync(api, team.Admin, "mr-viewer", "viewer");

        var view = await JsonAsync(await PutAsync(team.Admin, team.TargetId, "test", "application"), HttpStatusCode.OK);
        Assert.Equal((team.TargetId, "test", "application"),
            (view.GetProperty("agentId").GetGuid(), view.GetProperty("environment").GetString(), view.GetProperty("function").GetString()));
        Assert.Equal(team.Admin.Id, view.GetProperty("setBy").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, view.GetProperty("setAt").ValueKind);
        Assert.Equal("test", view.GetProperty("effectiveEnvironment").GetString());

        foreach (var reader in new[] { team.Admin, team.Owner, team.Other, viewer })
        {
            var got = await reader.GetJsonAsync($"/api/agents/{team.TargetId}/role");
            Assert.Equal(("test", "application", "test", "application"), (got.GetProperty("environment").GetString(),
                got.GetProperty("function").GetString(), got.GetProperty("effectiveEnvironment").GetString(), got.GetProperty("effectiveFunction").GetString()));
            var machine = await MachineAsync(reader, team);
            Assert.Equal(("test", "application", "test", "application"), (machine.GetProperty("roleEnvironment").GetString(),
                machine.GetProperty("roleFunction").GetString(), machine.GetProperty("effectiveEnvironment").GetString(), machine.GetProperty("effectiveFunction").GetString()));
        }
    }

    [Fact]
    public async Task A_role_change_is_audited_with_the_old_and_the_new_value_and_the_actor()
    {
        var team = await RemoteKit.TeamAsync(api);
        (await PutAsync(team.Admin, team.TargetId, "test", "application")).EnsureSuccessStatusCode();
        (await PutAsync(team.Admin, team.TargetId, "production", "database")).EnsureSuccessStatusCode();

        await using var db = api.Db();
        var events = await db.AuditEvents.AsNoTracking().Where(a => a.Action == "agent.role_changed" && a.TargetId == team.TargetId.ToString())
            .OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal((team.WorkspaceId, team.Admin.Id, "agent"), (e.WorkspaceId, e.ActorUserId, e.TargetType)));
        var first = events[0].Detail!.RootElement;
        Assert.Equal(JsonValueKind.Null, first.GetProperty("before").GetProperty("environment").ValueKind);
        Assert.Equal(("test", "application"), (first.GetProperty("after").GetProperty("environment").GetString(), first.GetProperty("after").GetProperty("function").GetString()));
        var second = events[1].Detail!.RootElement;
        Assert.Equal(("test", "application"), (second.GetProperty("before").GetProperty("environment").GetString(), second.GetProperty("before").GetProperty("function").GetString()));
        Assert.Equal(("production", "database"), (second.GetProperty("after").GetProperty("environment").GetString(), second.GetProperty("after").GetProperty("function").GetString()));
    }

    [Fact]
    public async Task Anyone_but_an_admin_of_the_workspace_gets_not_found_and_the_role_stays_unset()
    {
        var team = await RemoteKit.TeamAsync(api);
        var viewer = await RemoteKit.MemberAsync(api, team.Admin, "mr-viewer", "viewer");
        var outsider = await api.NewClient().SignedUpAsync("mr-outsider");
        foreach (var user in new[] { team.Owner, team.Other, viewer, outsider })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(user, team.TargetId, "test", "application")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Http.GetAsync($"/api/agents/{team.TargetId}/role")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(team.Admin, Guid.NewGuid(), "test", "application")).StatusCode);
        Assert.True(IsNull(await MachineAsync(team.Admin, team), "roleEnvironment"));
        await using var db = api.Db();
        Assert.False(await db.AuditEvents.AnyAsync(a => a.Action == "agent.role_changed" && a.TargetId == team.TargetId.ToString()));
    }

    [Fact]
    public async Task A_revoked_agent_has_no_role_to_read_or_set()
    {
        var team = await RemoteKit.TeamAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/agents/{team.TargetId}/revoke")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(team.Admin, team.TargetId, "test", "application")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await team.Admin.Http.GetAsync($"/api/agents/{team.TargetId}/role")).StatusCode);
    }

    [Fact]
    public async Task A_value_outside_the_closed_sets_is_a_bad_request_with_the_machine_role_code_and_changes_nothing()
    {
        var team = await RemoteKit.TeamAsync(api);
        (await PutAsync(team.Admin, team.TargetId, "test", "application")).EnsureSuccessStatusCode();

        var badEnvironment = await JsonAsync(await PutAsync(team.Admin, team.TargetId, "prod", "application"), HttpStatusCode.BadRequest);
        Assert.Equal("invalid_machine_role", badEnvironment.GetProperty("errors").GetProperty("environment")[0].GetString());
        var badFunction = await JsonAsync(await PutAsync(team.Admin, team.TargetId, "test", "mainframe"), HttpStatusCode.BadRequest);
        Assert.Equal("invalid_machine_role", badFunction.GetProperty("errors").GetProperty("function")[0].GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(team.Admin, team.TargetId, "", "application")).StatusCode);

        var still = await team.Admin.GetJsonAsync($"/api/agents/{team.TargetId}/role");
        Assert.Equal(("test", "application"), (still.GetProperty("environment").GetString(), still.GetProperty("function").GetString()));
    }

    [Fact]
    public async Task Sending_null_for_both_clears_the_role()
    {
        var team = await RemoteKit.TeamAsync(api);
        (await PutAsync(team.Admin, team.TargetId, "test", "application")).EnsureSuccessStatusCode();
        var cleared = await JsonAsync(await PutAsync(team.Admin, team.TargetId, null, null), HttpStatusCode.OK);
        Assert.True(IsNull(cleared, "environment") && IsNull(cleared, "function") && IsNull(cleared, "effectiveEnvironment"));
        var machine = await MachineAsync(team.Admin, team);
        Assert.True(IsNull(machine, "roleEnvironment") && IsNull(machine, "roleFunction"));
    }

    [Fact]
    public async Task Lowering_away_from_production_or_database_needs_a_recent_sign_in_and_raising_does_not()
    {
        var team = await RemoteKit.TeamAsync(api);
        (await PutAsync(team.Admin, team.TargetId, "production", "database")).EnsureSuccessStatusCode();
        api.Clock.Advance(TimeSpan.FromMinutes(11));

        var refused = await JsonAsync(await PutAsync(team.Admin, team.TargetId, "test", "application"), HttpStatusCode.Forbidden);
        Assert.Equal("reauth_required", refused.GetProperty("title").GetString());
        var kept = await team.Admin.GetJsonAsync($"/api/agents/{team.TargetId}/role");
        Assert.Equal(("production", "database"), (kept.GetProperty("environment").GetString(), kept.GetProperty("function").GetString()));
        // Only the function leaves database: still a lowering.
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(team.Admin, team.TargetId, "production", "application")).StatusCode);
        // Clearing the role is a lowering as well.
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(team.Admin, team.TargetId, null, null)).StatusCode);
        // Staying as strict, or tightening, needs no fresh sign-in.
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(team.Admin, team.TargetId, "production", "database")).StatusCode);

        var again = await SignedInAgainAsync(team.Admin);
        var lowered = await JsonAsync(await PutAsync(again, team.TargetId, "test", "application"), HttpStatusCode.OK);
        Assert.Equal("test", lowered.GetProperty("environment").GetString());
        api.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(again, team.TargetId, "production", "database")).StatusCode);
    }

    [Fact]
    public async Task An_old_sign_in_lowers_with_a_valid_two_step_code_and_not_with_a_wrong_or_missing_one()
    {
        var team = await RemoteKit.TeamAsync(api);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Admin);
        (await PutAsync(team.Admin, team.TargetId, "production", "database")).EnsureSuccessStatusCode();
        api.Clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(team.Admin, team.TargetId, "test", "application", "000000")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(team.Admin, team.TargetId, "test", "application")).StatusCode);
        var done = await JsonAsync(await PutAsync(team.Admin, team.TargetId, "test", "application", RemoteKit.TotpNow(api, secret)), HttpStatusCode.OK);
        Assert.Equal(("test", "application"), (done.GetProperty("environment").GetString(), done.GetProperty("function").GetString()));
    }

    [Fact]
    public async Task Moving_an_agent_to_another_workspace_clears_its_role_there()
    {
        var team = await RemoteKit.TeamAsync(api);
        (await PutAsync(team.Admin, team.TargetId, "production", "database")).EnsureSuccessStatusCode();

        var moved = await team.Owner.SendAsync(HttpMethod.Patch, $"/api/agents/{team.TargetId}", new { workspaceId = team.Owner.WorkspaceId });
        Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);

        var role = await team.Owner.GetJsonAsync($"/api/agents/{team.TargetId}/role");
        Assert.True(IsNull(role, "environment") && IsNull(role, "function") && IsNull(role, "setAt") && IsNull(role, "setBy"));
        await using var db = api.Db();
        var row = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == team.TargetId);
        Assert.Equal((null, null, null, null), (row.RoleEnvironment, row.RoleFunction, row.RoleSetAt, row.RoleSetBy));
    }
}
