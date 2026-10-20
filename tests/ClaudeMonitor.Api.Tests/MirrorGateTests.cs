using System.Net;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Integrations;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ClaudeMonitor.Api.Tests.Infrastructure.JiraWriteSprintKit;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The first steps of every write to the tool: an active connection, the person's own account, and the answers for each way it can fail (ADR-0015 §7).</summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorGateTests(ApiFactory api)
{
    private static (int Status, string? Title) Of(IResult result)
    {
        var problem = Assert.IsType<ProblemHttpResult>(result);
        return (problem.StatusCode, problem.ProblemDetails.Title);
    }

    [Theory]
    [InlineData(TrackerException.Codes.Forbidden, 403, "tracker_forbidden")]
    [InlineData(TrackerException.Codes.Unauthorized, 409, "jira_account_required")]
    [InlineData(TrackerException.Codes.Changed, 409, "jira_changed")]
    [InlineData(TrackerException.Codes.Rejected, 400, "tracker_rejected")]
    [InlineData(TrackerException.Codes.NotFound, 400, "tracker_not_found")]
    [InlineData(TrackerException.Codes.RateLimited, 429, "tracker_rate_limited")]
    [InlineData(TrackerException.Codes.Unavailable, 502, "tracker_unavailable")]
    [InlineData("a_code_nobody_knows", 502, "tracker_unavailable")]
    public void Each_failure_of_the_tool_has_its_answer_and_an_unknown_one_is_a_bad_gateway(string code, int status, string title) =>
        Assert.Equal((status, title), Of(MirrorGate.Refusal(new TrackerException(code))));

    [Fact]
    public void No_failure_is_needed_to_ask_and_nothing_is_asked_of_nothing() => Assert.Throws<ArgumentNullException>(() => MirrorGate.Refusal(null!));

    [Fact]
    public async Task A_disconnected_connection_refuses_every_write_as_detached_and_changes_nothing()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var issue = await s.W.IdAsync("WRT-2");
        var now = await s.IdAsync("Now");
        (await s.W.World.Admin.SendAsync(HttpMethod.Delete, $"/api/integrations/{s.W.World.ConnectionId}")).EnsureSuccessStatusCode();
        var calls = s.W.Jira.Calls(s.W.Site).Count;

        var answers = new[]
        {
            await JiraWriteKit.EditAsync(s.W.Member, issue, new { title = "Detached" }),
            await s.W.Member.SendAsync(HttpMethod.Put, $"/api/issues/{issue}/status", new { status = "done" }),
            await s.W.Member.PostAsync($"/api/projects/{s.W.Project}/issues", new { title = "New" }),
            await s.W.Member.PostAsync($"/api/projects/{s.W.Project}/sprints", new { name = "New" }),
            await SprintKit.AddAsync(s.W.Member, await s.IdAsync("Plan B"), issue),
            await SprintKit.CloseAsync(s.W.Member, now),
            await s.W.Member.SendAsync(HttpMethod.Delete, $"/api/sprints/{await s.IdAsync("Plan A")}"),
        };

        foreach (var answer in answers) await JiraWriteKit.AssertRefusedAsync(answer, HttpStatusCode.Conflict, "mirrored_detached");
        Assert.Equal(calls, s.W.Jira.Calls(s.W.Site).Count);
        Assert.Equal(("Story one", "active"), ((await s.W.RowAsync("WRT-2")).Title, await SprintKit.StatusAsync(s.W.Member, now)));
        Assert.Equal(s.W.World.ConnectionId, (await s.W.World.Admin.GetJsonAsync($"/api/projects/{s.W.Project}")).GetProperty("sourceConnectionId").GetGuid()); // detached, not forgotten
        Assert.Equal(HttpStatusCode.NoContent, (await JiraWriteKit.EditAsync(s.W.Member, issue, new { technicalDesign = "Podium keeps this" })).StatusCode);
    }

    [Fact]
    public async Task A_connection_that_needs_a_new_sign_in_refuses_writes_with_reauthorize()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        await using (var db = api.Db())
        {
            await db.IntegrationConnections.Where(c => c.Id == w.World.ConnectionId).ExecuteUpdateAsync(c => c.SetProperty(x => x.Status, ConnectionStatuses.Reauthorize));
        }

        var response = await JiraWriteKit.EditAsync(w.Member, await w.IdAsync("WRT-2"), new { title = "Stale" });

        await JiraWriteKit.AssertRefusedAsync(response, HttpStatusCode.Conflict, "reauthorize");
        Assert.Empty(w.Jira.Writes(w.Site));
    }

    [Fact]
    public async Task An_account_that_atlassian_no_longer_accepts_is_marked_for_a_new_sign_in_and_the_answer_is_account_required()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        w.MemberAccount!.Revoked = true;
        await ExpireAsync(w.Member.Id);

        var response = await JiraWriteKit.EditAsync(w.Member, await w.IdAsync("WRT-2"), new { technicalDesign = "x", priority = "5" });

        await JiraWriteKit.AssertRefusedAsync(response, HttpStatusCode.Conflict, "jira_account_required");
        await using var after = api.Db();
        Assert.Equal(AccountStatuses.Reauthorize, (await after.IntegrationAccounts.AsNoTracking().SingleAsync(a => a.UserId == w.Member.Id)).Status);
        Assert.Equal(ConnectionStatuses.Active, (await JiraKit.ConnectionAsync(w.World)).Status); // the connector's own sign-in still reads for everybody
        Assert.Empty(w.Jira.Writes(w.Site));
    }

    private async Task ExpireAsync(Guid user)
    {
        await using var db = api.Db();
        var past = api.Clock.GetUtcNow().AddHours(-1);
        await db.IntegrationAccounts.Where(a => a.UserId == user).ExecuteUpdateAsync(a => a.SetProperty(x => x.AccessExpiresAt, past));
    }

    [Fact]
    public async Task A_refused_edit_leaves_the_row_even_when_the_access_token_had_to_be_refreshed_first()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        await ExpireAsync(w.Member.Id);
        w.Site.Refuse(HttpMethod.Put, "/rest/api/3/issue/WRT-2", HttpStatusCode.Forbidden);

        var response = await JiraWriteKit.EditAsync(w.Member, await w.IdAsync("WRT-2"), new { title = "Refused" });

        await JiraWriteKit.AssertRefusedAsync(response, HttpStatusCode.Forbidden, "tracker_forbidden");
        Assert.Equal("Story one", (await w.RowAsync("WRT-2")).Title);
    }

    [Fact]
    public async Task A_refused_edit_leaves_the_row_when_atlassian_no_longer_accepts_the_account()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        w.MemberAccount!.Revoked = true;
        await ExpireAsync(w.Member.Id);

        var response = await JiraWriteKit.EditAsync(w.Member, await w.IdAsync("WRT-2"), new { title = "Revoked" });

        await JiraWriteKit.AssertRefusedAsync(response, HttpStatusCode.Conflict, "jira_account_required");
        Assert.Equal("Story one", (await w.RowAsync("WRT-2")).Title);
    }


    [Fact]
    public async Task Jira_asking_to_slow_down_is_passed_on_as_429_after_the_adapters_tries_and_the_row_stays()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        api.Jira.Fail(w.Site, Key("WRT-2"), HttpStatusCode.TooManyRequests, times: 3, retryAfter: "0");

        var response = await JiraWriteKit.EditAsync(w.Member, await w.IdAsync("WRT-2"), new { title = "Too fast" });

        await JiraWriteKit.AssertRefusedAsync(response, (HttpStatusCode)429, "tracker_rate_limited");
        Assert.Equal("Story one", (await w.RowAsync("WRT-2")).Title);
    }

    private static string Key(string key) => $"/rest/api/3/issue/{key}";

    [Fact]
    public async Task The_gate_opens_for_an_active_connection_and_a_person_with_an_account_and_says_why_it_cannot_otherwise()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var lonely = await RemoteKit.MemberAsync(api, w.World.Admin, "wr-lonely");
        await using var scope = api.Services.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<MirrorGate>();

        var (open, none) = await gate.OpenAsync(w.World.ConnectionId, w.Member.Id, CancellationToken.None);
        var (_, noAccount) = await gate.OpenAsync(w.World.ConnectionId, lonely.Id, CancellationToken.None);
        var (_, unknown) = await gate.OpenAsync(Guid.NewGuid(), w.Member.Id, CancellationToken.None);

        Assert.Null(none);
        Assert.Equal((w.Site.CloudId, w.World.ConnectionId), (open!.Ctx.SiteId, open.Connection.Id));
        Assert.Equal((409, "jira_account_required"), Of(noAccount!));
        Assert.Equal((409, "mirrored_detached"), Of(unknown!));
    }

    [Fact]
    public async Task The_account_to_assign_is_the_persons_own_first_then_the_jira_person_they_are_linked_to_then_none()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var linked = await RemoteKit.MemberAsync(api, w.World.Admin, "wr-linked");
        var lonely = await RemoteKit.MemberAsync(api, w.World.Admin, "wr-lonely");
        await using (var db = api.Db())
        {
            var person = await db.IntegrationPeople.SingleAsync(p => p.ConnectionId == w.World.ConnectionId && p.ExternalAccountId == "p-outsider");
            person.UserId = linked.Id;
            await db.SaveChangesAsync();
        }

        await using var scope = api.Services.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<MirrorGate>();
        var id = w.World.ConnectionId;

        Assert.Equal(w.MemberAccount!.Id, await gate.AccountOfAsync(id, w.Member.Id, CancellationToken.None));
        Assert.Equal("p-outsider", await gate.AccountOfAsync(id, linked.Id, CancellationToken.None));
        Assert.Null(await gate.AccountOfAsync(id, lonely.Id, CancellationToken.None));
        Assert.Equal("WRT", await gate.ProjectKeyAsync(w.Project, CancellationToken.None));
        Assert.Null(await gate.ProjectKeyAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
