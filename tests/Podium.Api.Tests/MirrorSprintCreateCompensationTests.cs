using System.Net;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using static Podium.Api.Tests.Infrastructure.JiraWriteSprintKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Podium.Api.Tests;

/// <summary>
/// A mirrored sprint is made in Jira first and saved here second (ADR-0015 §7). When the local save fails after Jira made it, the one Jira made is
/// deleted again (F-008); when Jira fails first, nothing was stored here, so there is nothing to undo.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorSprintCreateCompensationTests(ApiFactory api)
{
    private const string Sprints = "/rest/agile/1.0/sprint";

    private async Task<(SprintSet Set, TestUser Member, TracingLog Log, CommandFault Fault, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host)> FailingHostAsync()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        var fault = new CommandFault("INSERT INTO sprints") { Error = new InvalidOperationException("the database refused the write") };
        var log = new TracingLog();
        var host = HostKit.WithInterceptor(api, fault, log);
        return (s, await HostKit.SignInAsync(api, host, s.W.Member), log, fault, host);
    }

    [Fact]
    public async Task A_sprint_that_could_not_be_saved_here_is_deleted_again_in_jira_and_leaves_nothing_here()
    {
        var (s, member, _, _, host) = await FailingHostAsync();
        await using var _host = host;

        var response = await member.PostAsync($"/api/projects/{s.W.Project}/sprints", new { name = "Orphan", goal = "Never saved" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var made = s.W.Site.Sprints.Values.Single(x => x.Name == "Orphan");
        Assert.True(made.Deleted);
        Assert.Equal(["POST " + Sprints, "DELETE " + Route(made)], s.W.Jira.Writes(s.W.Site));
        await using var db = api.Db();
        Assert.False(await db.Sprints.AnyAsync(x => x.ProjectId == s.W.Project && x.Name == "Orphan"));
        Assert.False(await db.IntegrationLinks.AnyAsync(l => l.ConnectionId == s.W.World.ConnectionId && l.Kind == LinkKinds.Sprint && l.ExternalId == made.IdText));
    }

    [Fact]
    public async Task When_jira_refuses_the_delete_too_the_first_error_is_still_the_answer_and_the_loss_is_logged_without_the_sprints_text()
    {
        var (s, member, log, _, host) = await FailingHostAsync();
        await using var _host = host;
        s.W.Site.Refuse(HttpMethod.Delete, Sprints + "/", HttpStatusCode.Forbidden);

        var response = await member.PostAsync($"/api/projects/{s.W.Project}/sprints", new { name = "Stuck orphan", goal = "A private goal" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var made = s.W.Site.Sprints.Values.Single(x => x.Name == "Stuck orphan");
        Assert.False(made.Deleted);
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning && e.Category.EndsWith("SprintWrites", StringComparison.Ordinal));
        Assert.Contains(made.IdText, warning.Everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Stuck orphan", warning.Everything, StringComparison.Ordinal);
        Assert.DoesNotContain("A private goal", warning.Everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_jira_refuses_the_make_nothing_is_stored_here_and_no_delete_is_sent()
    {
        var s = await JiraWriteSprintKit.OpenAsync(api);
        s.W.Site.Refuse(HttpMethod.Post, Sprints, HttpStatusCode.Forbidden);
        var before = (await JiraKit.SprintsAsync(s.W.World, s.W.Project)).Count;

        var response = await s.W.Member.PostAsync($"/api/projects/{s.W.Project}/sprints", new { name = "Never made" });

        await JiraWriteKit.AssertRefusedAsync(response, HttpStatusCode.Forbidden, "tracker_forbidden");
        Assert.Equal(["POST " + Sprints], s.W.Jira.Writes(s.W.Site));
        Assert.Equal(before, (await JiraKit.SprintsAsync(s.W.World, s.W.Project)).Count);
        Assert.DoesNotContain(s.W.Site.Sprints.Values, x => x.Name == "Never made");
    }
}
