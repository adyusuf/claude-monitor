using System.Net;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Importing Jira spaces as projects with their backlog (ADR-0015 §4, §8): the project, the issues in Jira's order, epics and the answers.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationImportTests(ApiFactory api)
{
    [Fact]
    public async Task The_spaces_of_the_site_are_listed_with_their_imported_flag_for_any_member()
    {
        var w = await JiraKit.ConnectedAsync(api, site =>
        {
            site.Space("ABC", "Alpha").Description = "The alpha";
            site.Space("XYZ", "Zulu");
            for (var i = 0; i < 60; i++) site.Space($"M{i:D2}");
        });
        var viewer = await RemoteKit.MemberAsync(api, w.Admin, "ii-viewer", "viewer");
        var stranger = await api.NewClient().SignedUpAsync("ii-stranger");
        await JiraKit.ImportAsync(w, "ABC");
        var project = await JiraKit.ProjectIdAsync(w, "ABC");

        var spaces = (await viewer.GetJsonAsync($"{w.Base}/spaces")).EnumerateArray().ToList();

        Assert.Equal(62, spaces.Count);
        var alpha = spaces.Single(s => s.GetProperty("key").GetString() == "ABC");
        Assert.Equal(("Alpha", "The alpha", true, project), (alpha.GetProperty("name").GetString(), alpha.GetProperty("description").GetString(), alpha.GetProperty("imported").GetBoolean(), alpha.GetProperty("projectId").GetGuid()));
        var zulu = spaces.Single(s => s.GetProperty("key").GetString() == "XYZ");
        Assert.False(zulu.GetProperty("imported").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, zulu.GetProperty("projectId").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"{w.Base}/spaces")).StatusCode);
    }

    [Fact]
    public async Task An_imported_space_becomes_a_project_owned_by_the_importer_with_the_jira_settings()
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space("ABC", "Alpha").Description = "The alpha");

        var answer = await JiraKit.ImportAsync(w, "ABC");

        Assert.Equal(("ABC", "ok", 1, 0), (answer[0].GetProperty("key").GetString(), answer[0].GetProperty("outcome").GetString(), answer[0].GetProperty("created").GetInt32(), answer[0].GetProperty("updated").GetInt32()));
        var id = await JiraKit.ProjectIdAsync(w, "ABC");
        await using var db = api.Db();
        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.Equal(("Alpha", "The alpha", w.Admin.Id, w.Admin.Id, w.ConnectionId, ManagedStatuses.Active), (project.Name, project.Purpose, project.OwnerUserId, project.CreatedBy, project.SourceConnectionId, project.Status));
        Assert.Equal((PriorityScales.OneToFive, EstimationUnits.StoryPoints), (project.PriorityScale, project.EstimationUnit));
        Assert.Equal(WorkItemTypes.All, project.ItemTypes);
        Assert.Equal(HttpStatusCode.OK, (await w.Admin.Http.GetAsync($"/api/projects/{id}")).StatusCode);
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(a => a.WorkspaceId == w.WorkspaceId && a.Action == "integration.imported");
        Assert.Equal(w.ConnectionId.ToString(), audit.TargetId);
        Assert.DoesNotContain("Alpha", audit.Detail!.RootElement.ToString(), StringComparison.Ordinal); // ids and counts, never the tool's text
    }

    [Fact]
    public async Task A_name_that_a_project_already_has_gets_the_key_as_a_suffix()
    {
        var w = await JiraKit.ConnectedAsync(api, site => site.Space("ABC", "Taken Name"));
        await ProjectKit.CreateProjectAsync(w.Admin, "taken name");

        await JiraKit.ImportAsync(w, "ABC");

        await using var db = api.Db();
        Assert.Equal("Taken Name (ABC)", (await db.Projects.AsNoTracking().SingleAsync(p => p.SourceConnectionId == w.ConnectionId)).Name);
    }

    [Fact]
    public async Task Backlog_issues_are_numbered_in_jiras_order_and_their_fields_are_mapped()
    {
        var w = await JiraKit.ConnectedAsync(api, site =>
        {
            var s = site.Space("MAP", "Mapping");
            s.Issue("Login page", i => { i.Priority = "High"; i.Points = 5; i.Description = "Users sign in\nwith e-mail"; });
            s.Issue("Crash on save", i => { i.Type = "Bug"; i.Priority = "Highest"; i.Status = "In Progress"; i.Category = "indeterminate"; i.Points = 2.5; });
            s.Issue("Look at search", i => { i.Type = "Technical Spike"; i.Priority = "Low"; });
            s.Issue("Odd one", i => { i.Type = "Incident"; i.Priority = "Urgent"; i.Status = "In Review"; i.Category = "indeterminate"; });
            s.Issue("Sub work", i => { i.Type = "Sub-task"; i.IsSubtask = true; i.ParentKey = "MAP-1"; i.Description = "Details here"; });
            s.Issue("No priority", i => i.Priority = null);
            s.Issue("Finished long ago", i => { i.Category = "done"; i.Status = "Done"; });
        });

        await JiraKit.ImportAsync(w, "MAP");

        var issues = await JiraKit.IssuesAsync(w, await JiraKit.ProjectIdAsync(w, "MAP"));
        Assert.Equal(["Login page", "Crash on save", "Look at search", "Odd one", "Sub work", "No priority"], issues.Select(i => i.Title)); // the done one is not in the backlog
        Assert.Equal([1, 2, 3, 4, 5, 6], issues.Select(i => i.Number));
        Assert.Equal([1024.0, 2048, 3072, 4096, 5120, 6144], issues.Select(i => i.Position));
        Assert.Equal(["story", "bug", "spike", "task", "task", "story"], issues.Select(i => i.Type));
        Assert.Equal(["2", "1", "4", null, "3", null], issues.Select(i => i.Priority));
        Assert.Equal(["backlog", "in_progress", "backlog", "in_review", "backlog", "backlog"], issues.Select(i => i.Status));
        Assert.Equal(["5", "2.5", null, null, null, null], issues.Select(i => i.Estimate));
        Assert.Equal("Users sign in\nwith e-mail", issues[0].Description);
        Assert.Equal("Sub-task of MAP-1\n\nDetails here", issues[4].Description);
        Assert.Null(issues[4].ParentId); // a sub-task's parent is a story, not an epic
        Assert.Equal(Enumerable.Range(1, 6).Select(n => $"MAP-{n}"), issues.Select(i => i.ExternalKey));
        Assert.All(issues, i => Assert.Equal(($"https://{w.Site.Host}/browse/{i.ExternalKey}", w.Admin.Id, (Guid?)null, (Guid?)null), (i.ExternalUrl, i.CreatedBy, i.SprintId, i.AssigneeUserId)));
        Assert.Equal(6, (await IssueKit.TitlesAsync(w.Admin, $"/api/projects/{issues[0].ProjectId}/issues")).Count);
    }

    [Fact]
    public async Task A_long_backlog_is_read_over_several_pages_and_a_space_without_a_board_uses_the_search()
    {
        var w = await JiraKit.ConnectedAsync(api, site =>
        {
            var big = site.Space("BIG");
            for (var i = 0; i < 120; i++) big.Issue($"Item {i}");
            site.Space("NOB", board: false).Issue("From the search");
        });

        await JiraKit.ImportAsync(w, "BIG", "NOB");

        var big = await JiraKit.IssuesAsync(w, await JiraKit.ProjectIdAsync(w, "BIG"));
        Assert.Equal(Enumerable.Range(1, 120), big.Select(i => i.Number));
        Assert.Equal(Enumerable.Range(0, 120).Select(i => $"Item {i}"), big.Select(i => i.Title));
        Assert.Equal(big.Select(i => i.Position).Order(), big.Select(i => i.Position));
        Assert.Equal(["From the search"], (await JiraKit.IssuesAsync(w, await JiraKit.ProjectIdAsync(w, "NOB"))).Select(i => i.Title));
    }

    [Fact]
    public async Task Epics_are_parents_including_one_that_is_not_in_the_backlog_and_fetched_on_its_own()
    {
        var w = await JiraKit.ConnectedAsync(api, site =>
        {
            var s = site.Space("EPC");
            s.Issue("Old epic", i => { i.Type = "Epic"; i.Category = "done"; i.Status = "Done"; });
            s.Issue("Payments", i => i.Type = "Epic");
            s.Issue("Child of payments", i => i.ParentKey = "EPC-2");
            s.Issue("Child of the old one", i => i.ParentKey = "EPC-1");
            s.Issue("Under a story", i => i.ParentKey = "EPC-3");
        });

        await JiraKit.ImportAsync(w, "EPC");

        var issues = (await JiraKit.IssuesAsync(w, await JiraKit.ProjectIdAsync(w, "EPC"))).ToDictionary(i => i.Title);
        Assert.Equal(1, issues["Old epic"].Number); // fetched on its own and placed first
        Assert.Equal(("epic", "done"), (issues["Old epic"].Type, issues["Old epic"].Status));
        Assert.Equal(issues["Payments"].Id, issues["Child of payments"].ParentId);
        Assert.Equal(issues["Old epic"].Id, issues["Child of the old one"].ParentId);
        Assert.Null(issues["Under a story"].ParentId);
        Assert.Equal(1, api.Jira.Count(w.Site, "/rest/api/3/issue/EPC-1"));
    }

    [Fact]
    public async Task An_epic_ranked_after_its_child_is_still_the_parent_once_the_issues_are_read_again()
    {
        var w = await JiraKit.ConnectedAsync(api, site =>
        {
            var s = site.Space("ORD");
            s.Issue("Child first", i => i.ParentKey = "ORD-2");
            s.Issue("Late epic", i => i.Type = "Epic");
        });

        await JiraKit.ImportAsync(w, "ORD");
        await JiraKit.PassAsync(w); // the pass places what the first read could not

        var issues = (await JiraKit.IssuesAsync(w, await JiraKit.ProjectIdAsync(w, "ORD"))).ToDictionary(i => i.Title);
        Assert.Equal(issues["Late epic"].Id, issues["Child first"].ParentId);
    }

    [Fact]
    public async Task An_epic_ranked_after_its_child_is_the_parent_straight_after_the_first_import()
    {
        var w = await JiraKit.ConnectedAsync(api, site =>
        {
            var s = site.Space("ORF");
            s.Issue("Child first", i => i.ParentKey = "ORF-2");
            s.Issue("Late epic", i => i.Type = "Epic");
        });

        await JiraKit.ImportAsync(w, "ORF");

        var issues = (await JiraKit.IssuesAsync(w, await JiraKit.ProjectIdAsync(w, "ORF"))).ToDictionary(i => i.Title);
        Assert.Equal(issues["Late epic"].Id, issues["Child first"].ParentId);
    }

    [Fact]
    public async Task Importing_again_changes_no_row_count_and_reads_a_changed_issue_in_place()
    {
        FakeIssue? changed = null;
        var w = await JiraKit.ConnectedAsync(api, site =>
        {
            var s = site.Space("RE", "Again");
            changed = s.Issue("First");
            s.Issue("Second");
        });
        await JiraKit.ImportAsync(w, "RE");
        var project = await JiraKit.ProjectIdAsync(w, "RE");
        var before = await JiraKit.IssuesAsync(w, project);

        var same = await JiraKit.ImportAsync(w, "RE");
        changed!.Summary = "First, renamed";
        changed.Status = "In Progress";
        changed.Category = "indeterminate";
        api.Jira.Touch(changed);
        var edited = await JiraKit.ImportAsync(w, "RE");

        Assert.Equal((0, 1), (same[0].GetProperty("created").GetInt32(), same[0].GetProperty("updated").GetInt32())); // the project only
        Assert.Equal((0, 2), (edited[0].GetProperty("created").GetInt32(), edited[0].GetProperty("updated").GetInt32())); // the project and the issue
        var after = await JiraKit.IssuesAsync(w, project);
        Assert.Equal(before.Select(i => i.Id), after.Select(i => i.Id));
        Assert.Equal(["First, renamed", "Second"], after.Select(i => i.Title));
        Assert.Equal("in_progress", after[0].Status);
        await using var db = api.Db();
        Assert.Equal(3, await db.IntegrationLinks.CountAsync(l => l.ConnectionId == w.ConnectionId)); // the project and two issues
        Assert.Equal(1, await db.Projects.CountAsync(p => p.SourceConnectionId == w.ConnectionId));
    }

    [Fact]
    public async Task A_space_that_does_not_exist_or_cannot_be_read_is_reported_and_the_others_still_import()
    {
        var w = await JiraKit.ConnectedAsync(api, site =>
        {
            site.Space("OKK").Issue("Fine");
            site.Space("BAD").Issue("Unreadable");
        });
        api.Jira.Fail(w.Site, "/rest/api/3/project/BAD", HttpStatusCode.InternalServerError);

        var answer = (await JiraKit.ImportAsync(w, "NOPE", "OKK", "BAD")).EnumerateArray().Select(a => (a.GetProperty("key").GetString(), a.GetProperty("outcome").GetString())).ToList();

        Assert.Equal([("NOPE", "space_not_found"), ("OKK", "ok"), ("BAD", "tracker_unavailable")], answer);
        await using var db = api.Db();
        Assert.Equal(["OKK"], await db.IntegrationLinks.Where(l => l.ConnectionId == w.ConnectionId && l.Kind == LinkKinds.Project).Select(l => l.ExternalKey!).ToListAsync());
        var runs = await db.IntegrationSyncRuns.AsNoTracking().Where(r => r.ConnectionId == w.ConnectionId).ToListAsync();
        Assert.Equal(3, runs.Count);
        Assert.Equal([("failed", "space_not_found"), ("failed", "tracker_unavailable"), ("ok", "")], runs.Select(r => (r.Status, r.Error ?? "")).Order()); // one run per space
        Assert.All(runs, r => Assert.Equal("import", r.Kind));
        Assert.All(runs, r => Assert.NotNull(r.EndedAt));
        var shown = (await w.Admin.GetJsonAsync($"{w.Base}/runs")).EnumerateArray().ToList();
        Assert.Equal(3, shown.Count);
        Assert.DoesNotContain("secret detail", shown.Select(r => r.ToString()).Aggregate("", (a, b) => a + b), StringComparison.Ordinal);
    }
}
