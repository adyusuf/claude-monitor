using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>How a grant is asked for, decided and given (ADR-0005): a request is only a request until the owner re-authenticates.</summary>
[Collection(ApiGroup.Name)]
public sealed class GrantApprovalTests(ApiFactory api)
{
    private async Task<MachineGrant> GrantRowAsync(Guid id)
    {
        await using var db = api.Db();
        return await db.MachineGrants.AsNoTracking().SingleAsync(g => g.Id == id);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_request_with_a_template_the_matcher_refuses_is_a_bad_template_with_the_matchers_code()
    {
        var team = await RemoteKit.TeamAsync(api);
        var good = RemoteKit.GrantAsk(team.TargetId);
        var bad = new (GrantRequest Ask, string Code)[]
        {
            (good with { Template = ["/bin/sh", "-c", "x"] }, GrantErrors.NeverGrantable),
            (good with { Template = ["tail", "-n"] }, GrantErrors.Argv0),
            (good with { Template = ["/usr/bin/find", "/var/log", "-exec"] }, GrantErrors.ExecOption),
            (good with { Template = ["/usr/bin/tail", "{path:/etc/}"] }, GrantErrors.Root),
            (good with { Template = ["/usr/bin/tail", "{word}{word}"] }, GrantErrors.Placeholder),
            (good with { Cwd = "relative" }, GrantErrors.Cwd),
            (good with { MaxTimeoutSeconds = 3601 }, GrantErrors.Timeout),
        };
        foreach (var (ask, code) in bad)
        {
            var body = await BodyAsync(await team.Requester.Http.PostAsJsonAsync("/api/agent/grants", ask, TestUser.Json), HttpStatusCode.BadRequest);
            Assert.Equal((RemoteErrors.BadTemplate, code), (body.GetProperty("title").GetString(), body.GetProperty("detail").GetString()));
        }

        await using var db = api.Db();
        Assert.False(await db.MachineGrants.AnyAsync(g => g.TargetAgentId == team.TargetId));
    }

    [Fact]
    public async Task A_request_is_refused_for_its_shape_its_switch_its_target_and_its_requesters_role()
    {
        var team = await RemoteKit.TeamAsync(api);
        var good = RemoteKit.GrantAsk(team.TargetId);
        foreach (var shaped in new[] { good with { Days = 0 }, good with { Days = 91 }, good with { Reason = new string('r', 501) } })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await team.Requester.Http.PostAsJsonAsync("/api/agent/grants", shaped, TestUser.Json)).StatusCode);
        }

        Assert.Equal(GrantStatuses.Requested, (await RemoteKit.RequestGrantAsync(team.Requester, good with { Days = 90 })).Status);
        Assert.Equal(HttpStatusCode.NotFound,
            (await team.Requester.Http.PostAsJsonAsync("/api/agent/grants", good with { TargetAgentId = Guid.NewGuid() }, TestUser.Json)).StatusCode);
        await RemoteKit.SetSwitchAsync(team.Admin, false);
        await RemoteKit.AssertProblemAsync(await team.Requester.Http.PostAsJsonAsync("/api/agent/grants", good, TestUser.Json),
            HttpStatusCode.Conflict, RemoteErrors.Disabled);
    }

    [Fact]
    public async Task Asking_for_the_same_grant_again_returns_the_live_one_and_ten_requests_wait_per_target()
    {
        var team = await RemoteKit.TeamAsync(api);
        var first = await RemoteKit.RequestGrantAsync(team.Requester, RemoteKit.GrantAsk(team.TargetId));
        Assert.Equal(first.Id, (await RemoteKit.RequestGrantAsync(team.Requester, RemoteKit.GrantAsk(team.TargetId, reason: "other words"))).Id);
        for (var max = 101; max < 110; max++) await RemoteKit.RequestGrantAsync(team.Requester, RemoteKit.GrantAsk(team.TargetId, max));

        await RemoteKit.AssertProblemAsync(
            await team.Requester.Http.PostAsJsonAsync("/api/agent/grants", RemoteKit.GrantAsk(team.TargetId, 200), TestUser.Json),
            HttpStatusCode.Conflict, RemoteErrors.TargetBusy);
        Assert.Equal(first.Id, (await RemoteKit.RequestGrantAsync(team.Requester, RemoteKit.GrantAsk(team.TargetId))).Id);
    }

    [Fact]
    public async Task Only_the_target_owner_approves_and_a_viewer_agent_cannot_ask()
    {
        var team = await RemoteKit.TeamAsync(api);
        var grant = await RemoteKit.RequestGrantAsync(team.Requester, RemoteKit.GrantAsk(team.TargetId));
        foreach (var notOwner in new[] { team.Admin, team.Other })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await notOwner.PostAsync($"/api/grants/{grant.Id}/approve", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await notOwner.PostAsync($"/api/grants/{grant.Id}/deny")).StatusCode);
        }

        Assert.Equal(GrantStatuses.Requested, (await GrantRowAsync(grant.Id)).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/grants/{grant.Id}/approve", new { })).StatusCode);
        var row = await GrantRowAsync(grant.Id);
        Assert.Equal((GrantStatuses.Active, team.Owner.Id), (row.Status, row.DecidedBy));
        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync($"/api/grants/{grant.Id}/approve", new { }), HttpStatusCode.Conflict, RemoteErrors.NotPending);
        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync($"/api/grants/{grant.Id}/deny"), HttpStatusCode.Conflict, RemoteErrors.NotPending);

        var asker = await RemoteKit.MemberAsync(api, team.Admin, "grant-viewer");
        var askerAgent = await asker.ConnectAgentAsync(team.WorkspaceId);
        (await team.Admin.SendAsync(HttpMethod.Patch, $"/api/workspaces/{team.WorkspaceId}/members/{asker.Id}", new { role = "viewer" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound,
            (await askerAgent.Http.PostAsJsonAsync("/api/agent/grants", RemoteKit.GrantAsk(team.TargetId), TestUser.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await askerAgent.Http.GetAsync("/api/agent/grants")).StatusCode);
    }

    [Fact]
    public async Task A_denied_grant_never_matches_and_a_denial_is_the_owners_alone()
    {
        var team = await RemoteKit.TeamAsync(api);
        var grant = await RemoteKit.RequestGrantAsync(team.Requester, RemoteKit.GrantAsk(team.TargetId));
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/grants/{grant.Id}/deny")).StatusCode);
        Assert.Equal(GrantStatuses.Denied, (await GrantRowAsync(grant.Id)).Status);
        Assert.Equal(RunStatuses.PendingApproval, (await RemoteKit.CreateAsync(team.Requester, RemoteKit.GrantRun(team.TargetId))).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await team.Owner.PostAsync($"/api/grants/{grant.Id}/approve", new { })).StatusCode);
    }

    [Fact]
    public async Task Approving_needs_a_sign_in_within_ten_minutes_and_a_fresh_sign_in_renews_it()
    {
        var team = await RemoteKit.TeamAsync(api);
        var grant = await RemoteKit.RequestGrantAsync(team.Requester, RemoteKit.GrantAsk(team.TargetId));
        api.Clock.Advance(TimeSpan.FromMinutes(11));
        var refused = await BodyAsync(await team.Owner.PostAsync($"/api/grants/{grant.Id}/approve", new { }), HttpStatusCode.Forbidden);
        Assert.Equal("reauth_required", refused.GetProperty("title").GetString());
        Assert.Equal(GrantStatuses.Requested, (await GrantRowAsync(grant.Id)).Status);

        var again = api.NewClient();
        (await again.PostAsync("/api/auth/login", new { email = team.Owner.Email, password = TestUser.Password })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await again.PostAsync($"/api/grants/{grant.Id}/approve", new { })).StatusCode);
        Assert.Equal(GrantStatuses.Active, (await GrantRowAsync(grant.Id)).Status);
    }

    [Fact]
    public async Task An_old_session_approves_with_a_valid_two_step_code_and_not_with_a_wrong_one()
    {
        var team = await RemoteKit.TeamAsync(api);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        var grant = await RemoteKit.RequestGrantAsync(team.Requester, RemoteKit.GrantAsk(team.TargetId));
        api.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.Forbidden, (await team.Owner.PostAsync($"/api/grants/{grant.Id}/approve", new { code = "000000" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await team.Owner.PostAsync($"/api/grants/{grant.Id}/approve", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await team.Owner.PostAsync($"/api/grants/{grant.Id}/approve", new { code = RemoteKit.TotpNow(api, secret) })).StatusCode);
    }

    [Fact]
    public async Task The_owner_gives_a_grant_directly_after_signing_in_and_it_is_active_at_once()
    {
        var team = await RemoteKit.TeamAsync(api);
        var ask = RemoteKit.GrantAsk(team.TargetId);
        var body = new { template = ask.Template.ToArray(), cwd = ask.Cwd, maxTimeoutSeconds = 60, days = 7, granteeUserId = team.Admin.Id };
        Assert.Equal(HttpStatusCode.NotFound, (await team.Admin.PostAsync($"/api/agents/{team.TargetId}/grants", body)).StatusCode);

        var gave = await BodyAsync(await team.Owner.PostAsync($"/api/agents/{team.TargetId}/grants", body), HttpStatusCode.OK);
        Assert.Equal((GrantStatuses.Active, team.Admin.Id), (gave.GetProperty("status").GetString(), gave.GetProperty("granteeUserId").GetGuid()));
        Assert.Equal(team.Owner.Id, gave.GetProperty("ownerUserId").GetGuid());
        var again = await team.Owner.PostAsync($"/api/agents/{team.TargetId}/grants", body);
        Assert.Equal(gave.GetProperty("id").GetGuid(), (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());

        var outsider = await api.NewClient().SignedUpAsync("grant-outsider");
        Assert.Equal(HttpStatusCode.BadRequest, (await team.Owner.PostAsync($"/api/agents/{team.TargetId}/grants", body with { granteeUserId = outsider.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await team.Owner.PostAsync($"/api/agents/{team.TargetId}/grants", body with { days = 91 })).StatusCode);
        var shell = await BodyAsync(await team.Owner.PostAsync($"/api/agents/{team.TargetId}/grants",
            body with { template = new[] { "/bin/bash", "x" } }), HttpStatusCode.BadRequest);
        Assert.Equal(GrantErrors.NeverGrantable, shell.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Giving_a_grant_directly_needs_a_recent_sign_in_too()
    {
        var team = await RemoteKit.TeamAsync(api);
        var ask = RemoteKit.GrantAsk(team.TargetId);
        var body = new { template = ask.Template.ToArray(), cwd = ask.Cwd, maxTimeoutSeconds = 60, days = 7 };
        api.Clock.Advance(TimeSpan.FromMinutes(11));
        var refused = await BodyAsync(await team.Owner.PostAsync($"/api/agents/{team.TargetId}/grants", body), HttpStatusCode.Forbidden);
        Assert.Equal("reauth_required", refused.GetProperty("title").GetString());
        await using var db = api.Db();
        Assert.False(await db.MachineGrants.AnyAsync(g => g.TargetAgentId == team.TargetId));
    }

    [Fact]
    public async Task An_approval_after_the_requested_grant_expired_is_a_conflict_and_leaves_it_requested()
    {
        var team = await RemoteKit.TeamAsync(api);
        var stale = await RemoteKit.RequestGrantAsync(team.Requester, RemoteKit.GrantAsk(team.TargetId, days: 1));
        api.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        var again = api.NewClient();
        (await again.PostAsync("/api/auth/login", new { email = team.Owner.Email, password = TestUser.Password })).EnsureSuccessStatusCode();
        await RemoteKit.AssertProblemAsync(await again.PostAsync($"/api/grants/{stale.Id}/approve", new { }), HttpStatusCode.Conflict, RemoteErrors.NotPending);
        Assert.Equal(GrantStatuses.Requested, (await GrantRowAsync(stale.Id)).Status);
    }
}
