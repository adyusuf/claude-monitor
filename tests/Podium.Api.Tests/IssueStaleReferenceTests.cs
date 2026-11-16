using System.Net;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>Editing an issue validates only the references it changes: one that went stale since (a demoted assignee, an archived parent, application or component, a cancelled release) no longer blocks every edit (F-102 A).</summary>
[Collection(ApiGroup.Name)]
public sealed class IssueStaleReferenceTests(ApiFactory api)
{
    private sealed record Stale(IssueTeam Team, Guid Issue, Guid Epic);

    /// <summary>An issue that points at an epic, a person, an application, a component and a release.</summary>
    private async Task<Stale> IssueWithReferencesAsync()
    {
        var t = await IssueKit.TeamAsync(api);
        var epic = await IssueKit.IssueAsync(t.Owner, t.Project, "Epic", "epic");
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, new
        {
            title = "Story",
            type = "story",
            assigneeUserId = t.Contributor.Id,
            parentId = epic,
            applicationId = t.Application,
            componentId = t.Component,
            releaseId = t.Release,
        });
        return new Stale(t, issue, epic);
    }

    private static async Task<HttpResponseMessage> RenameAsync(IssueTeam t, Guid issue, object? more = null) =>
        await JiraWriteKit.EditAsync(t.Owner, issue, IssueKit.Merge(new Dictionary<string, object?> { ["title"] = "Renamed" }, more));

    [Fact]
    public async Task A_demoted_assignee_does_not_block_an_edit_that_leaves_the_assignee_alone()
    {
        var s = await IssueWithReferencesAsync();
        await using (var db = api.Db())
        {
            await db.WorkspaceMembers.Where(m => m.UserId == s.Team.Contributor.Id).ExecuteUpdateAsync(m => m.SetProperty(x => x.Role, Roles.Viewer));
        }

        Assert.Equal(HttpStatusCode.NoContent, (await RenameAsync(s.Team, s.Issue)).StatusCode);
        Assert.Equal("Renamed", (await IssueKit.ViewAsync(s.Team.Owner, s.Issue)).GetProperty("issue").GetProperty("title").GetString());
        Assert.Equal(s.Team.Contributor.Id, (await IssueKit.ViewAsync(s.Team.Owner, s.Issue)).GetProperty("issue").GetProperty("assigneeUserId").GetGuid());
    }

    [Fact]
    public async Task An_archived_parent_application_component_and_a_cancelled_release_do_not_block_an_unrelated_edit()
    {
        var s = await IssueWithReferencesAsync();
        (await s.Team.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{s.Epic}")).EnsureSuccessStatusCode();
        (await s.Team.Owner.SendAsync(HttpMethod.Delete, $"/api/components/{s.Team.Component}")).EnsureSuccessStatusCode();
        (await s.Team.Owner.SendAsync(HttpMethod.Delete, $"/api/releases/{s.Team.Release}")).EnsureSuccessStatusCode();
        (await s.Team.Owner.SendAsync(HttpMethod.Delete, $"/api/applications/{s.Team.Application}")).EnsureSuccessStatusCode();

        var response = await RenameAsync(s.Team, s.Issue);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var read = (await IssueKit.ViewAsync(s.Team.Owner, s.Issue)).GetProperty("issue");
        Assert.Equal((s.Epic, s.Team.Application, s.Team.Component, s.Team.Release), (read.GetProperty("parentId").GetGuid(), read.GetProperty("applicationId").GetGuid(), read.GetProperty("componentId").GetGuid(), read.GetProperty("releaseId").GetGuid()));
    }

    [Fact]
    public async Task A_reference_that_is_changed_is_still_checked_against_what_is_usable_now()
    {
        var s = await IssueWithReferencesAsync();
        var archivedEpic = await IssueKit.IssueAsync(s.Team.Owner, s.Team.Project, "Old epic", "epic");
        (await s.Team.Owner.SendAsync(HttpMethod.Delete, $"/api/issues/{archivedEpic}")).EnsureSuccessStatusCode();
        var cancelled = (await IssueKit.Created(await s.Team.Owner.PostAsync($"/api/applications/{s.Team.Application}/releases", new { name = "2.0.0" }))).GetProperty("id").GetGuid();
        (await s.Team.Owner.SendAsync(HttpMethod.Delete, $"/api/releases/{cancelled}")).EnsureSuccessStatusCode();
        var viewer = s.Team.Viewer.Id;
        var stranger = (await api.NewClient().SignedUpAsync("is-stale-outsider")).Id;

        Assert.Equal(("assigneeUserId", "not_a_member"), await ProjectKit.ProblemAsync(await RenameAsync(s.Team, s.Issue, new { assigneeUserId = viewer })));
        Assert.Equal(("assigneeUserId", "not_a_member"), await ProjectKit.ProblemAsync(await RenameAsync(s.Team, s.Issue, new { assigneeUserId = stranger })));
        Assert.Equal(("parentId", "invalid_parent"), await ProjectKit.ProblemAsync(await RenameAsync(s.Team, s.Issue, new { parentId = archivedEpic })));
        Assert.Equal(("releaseId", "invalid_release"), await ProjectKit.ProblemAsync(await RenameAsync(s.Team, s.Issue, new { releaseId = cancelled })));
        Assert.Equal(("componentId", "invalid_component"), await ProjectKit.ProblemAsync(await RenameAsync(s.Team, s.Issue, new { componentId = Guid.NewGuid() })));
        Assert.Equal(("applicationId", "invalid_application"), await ProjectKit.ProblemAsync(await RenameAsync(s.Team, s.Issue, new { applicationId = Guid.NewGuid() })));
    }

    [Fact]
    public async Task Moving_to_another_application_checks_the_component_and_release_that_stay_against_the_new_one()
    {
        var s = await IssueWithReferencesAsync();
        var other = (await ProjectKit.CreateApplicationAsync(s.Team.Owner, s.Team.Project, "Mobile")).GetProperty("id").GetGuid();

        var response = await RenameAsync(s.Team, s.Issue, new { applicationId = other });

        Assert.Equal(("componentId", "invalid_component"), await ProjectKit.ProblemAsync(response));
    }

    [Fact]
    public async Task A_new_issue_still_has_every_reference_checked()
    {
        var s = await IssueWithReferencesAsync();
        (await s.Team.Owner.SendAsync(HttpMethod.Delete, $"/api/releases/{s.Team.Release}")).EnsureSuccessStatusCode();

        var response = await s.Team.Owner.PostAsync($"/api/projects/{s.Team.Project}/issues", new { title = "New", type = "story", applicationId = s.Team.Application, releaseId = s.Team.Release });

        Assert.Equal(("releaseId", "invalid_release"), await ProjectKit.ProblemAsync(response));
    }

    [Fact]
    public async Task A_type_priority_or_estimate_the_project_no_longer_offers_does_not_block_an_edit_that_keeps_it()
    {
        var t = await IssueKit.TeamAsync(api);
        var issue = await IssueKit.IssueAsync(t.Owner, t.Project, new { title = "Old way", type = "bug", priority = "must", estimate = "5" });
        await using (var db = api.Db())
        {
            await db.Projects.Where(p => p.Id == t.Project).ExecuteUpdateAsync(p => p.SetProperty(x => x.ItemTypes, ["epic", "story"]).SetProperty(x => x.PriorityScale, PriorityScales.P0ToP3).SetProperty(x => x.EstimationUnit, EstimationUnits.TShirt));
        }

        var kept = await JiraWriteKit.EditAsync(t.Owner, issue, new { title = "Still editable" });

        Assert.Equal(HttpStatusCode.NoContent, kept.StatusCode);
        var read = (await IssueKit.ViewAsync(t.Owner, issue)).GetProperty("issue");
        Assert.Equal(("Still editable", "bug", "must", "5"), (read.GetProperty("title").GetString(), read.GetProperty("type").GetString(), read.GetProperty("priority").GetString(), read.GetProperty("estimate").GetString()));
        Assert.Equal(("priority", "invalid_value"), await ProjectKit.ProblemAsync(await JiraWriteKit.EditAsync(t.Owner, issue, new { priority = "should" })));
        Assert.Equal(("estimate", "invalid_value"), await ProjectKit.ProblemAsync(await JiraWriteKit.EditAsync(t.Owner, issue, new { estimate = "7" })));
        Assert.Equal(("type", "invalid_value"), await ProjectKit.ProblemAsync(await JiraWriteKit.EditAsync(t.Owner, issue, new { type = "task" })));
    }
}
