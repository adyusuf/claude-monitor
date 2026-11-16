using System.Net;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// Detaching a disconnected project also cuts its mirrored sprints loose (their source and their link rows), so a sprint can be edited, started,
/// closed and archived here without a call to the tool; before the detach the old refusal still holds, and another project's sprints stay mirrored.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorDetachSprintTests(ApiFactory api)
{
    private sealed record World(WriteWorld W, FakeSprint Plan, FakeSprint Spare, FakeSprint Now, FakeSprint Other, Guid OtherProject);

    private async Task<World> OpenAsync()
    {
        FakeSprint? plan = null, spare = null, now = null, other = null;
        var w = await JiraWriteKit.OpenAsync(api, (site, space) =>
        {
            plan = site.Sprint(space, "Plan A", "future", "2026-11-02", "2026-11-13");
            spare = site.Sprint(space, "Plan B", "future");
            now = site.Sprint(space, "Now", "active", "2026-10-15", "2026-10-28");
            space.Issue("Waiting in now", i => i.SprintId = now.Id);
            var elsewhere = site.Space("OTH", "Other space");
            other = site.Sprint(elsewhere, "Other plan", "future");
        });
        await JiraKit.ImportSprintsAsync(w.World, JiraWriteKit.Key, plan!.Id, spare!.Id, now!.Id);
        await JiraKit.ImportAsync(w.World, "OTH");
        await JiraKit.ImportSprintsAsync(w.World, "OTH", other!.Id);
        return new World(w, plan, spare, now, other, await JiraKit.ProjectIdAsync(w.World, "OTH"));
    }

    private static async Task DisconnectAsync(World s) =>
        (await s.W.World.Admin.SendAsync(HttpMethod.Delete, $"/api/integrations/{s.W.World.ConnectionId}")).EnsureSuccessStatusCode();

    private async Task<Guid> IdAsync(Guid project, string name)
    {
        await using var db = api.Db();
        return await db.Sprints.AsNoTracking().Where(s => s.ProjectId == project && s.Name == name).Select(s => s.Id).SingleAsync();
    }

    [Fact]
    public async Task Before_the_detach_a_sprint_of_a_disconnected_project_is_still_refused_as_detached_and_nothing_is_called()
    {
        var s = await OpenAsync();
        await DisconnectAsync(s);
        var calls = s.W.Jira.Calls(s.W.Site).Count;

        await JiraWriteKit.AssertRefusedAsync(await JiraWriteSprintKit.EditAsync(s.W.Member, await IdAsync(s.W.Project, "Plan A"), new { name = "Ours" }), HttpStatusCode.Conflict, "mirrored_detached");
        await JiraWriteKit.AssertRefusedAsync(await SprintKit.CloseAsync(s.W.Member, await IdAsync(s.W.Project, "Now")), HttpStatusCode.Conflict, "mirrored_detached");

        Assert.Equal(calls, s.W.Jira.Calls(s.W.Site).Count);
    }

    [Fact]
    public async Task After_the_detach_the_sprints_are_edited_closed_filled_and_archived_here_without_a_call_to_the_tool()
    {
        var s = await OpenAsync();
        await DisconnectAsync(s);
        (await s.W.World.Admin.PostAsync($"/api/projects/{s.W.Project}/detach")).EnsureSuccessStatusCode();
        var calls = s.W.Jira.Calls(s.W.Site).Count;
        var plan = await IdAsync(s.W.Project, "Plan A");
        var spare = await IdAsync(s.W.Project, "Plan B");
        var now = await IdAsync(s.W.Project, "Now");
        var backlogged = (await JiraKit.IssueAsync(s.W.World, "WRT-2")).Id;

        var edited = await JiraWriteSprintKit.EditAsync(s.W.Member, plan, new { name = "Ours" });
        var closed = await SprintKit.CloseAsync(s.W.Member, now, plan);
        var added = await SprintKit.AddAsync(s.W.Member, spare, backlogged);
        var archived = await s.W.Member.SendAsync(HttpMethod.Delete, $"/api/sprints/{spare}");

        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.NoContent],
            new[] { edited.StatusCode, closed.StatusCode, added.StatusCode, archived.StatusCode });
        Assert.Equal(calls, s.W.Jira.Calls(s.W.Site).Count);
        Assert.Equal(("Ours", "closed"), ((await JiraWriteSprintKit.ViewAsync(s.W.Member, plan)).GetProperty("name").GetString(), await SprintKit.StatusAsync(s.W.Member, now)));
    }

    [Fact]
    public async Task The_detach_clears_the_source_and_the_links_of_this_projects_sprints_only_and_counts_them_in_the_audit()
    {
        var s = await OpenAsync();
        await DisconnectAsync(s);

        (await s.W.World.Admin.PostAsync($"/api/projects/{s.W.Project}/detach")).EnsureSuccessStatusCode();

        await using var db = api.Db();
        var mine = await db.Sprints.AsNoTracking().Where(x => x.ProjectId == s.W.Project).ToListAsync();
        Assert.Equal(3, mine.Count);
        Assert.All(mine, x => Assert.Null(x.SourceConnectionId));
        Assert.False(await db.IntegrationLinks.AnyAsync(l => l.Kind == LinkKinds.Sprint && mine.Select(x => x.Id).Contains(l.LocalId)));
        var other = await db.Sprints.AsNoTracking().SingleAsync(x => x.ProjectId == s.OtherProject);
        Assert.Equal(s.W.World.ConnectionId, other.SourceConnectionId);
        Assert.True(await db.IntegrationLinks.AnyAsync(l => l.Kind == LinkKinds.Sprint && l.LocalId == other.Id));
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.TargetType == "project" && a.TargetId == s.W.Project.ToString() && a.Action == AuditActions.ProjectDetached);
        Assert.Equal(3, audit.Detail!.RootElement.GetProperty("sprints").GetInt32());
    }
}
