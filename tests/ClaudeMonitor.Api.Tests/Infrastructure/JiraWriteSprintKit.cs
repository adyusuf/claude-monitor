using System.Text.Json;
using ClaudeMonitor.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests.Infrastructure;

/// <summary>
/// <see cref="WriteWorld"/> with three imported sprints: "Plan A" (planned, dated, with WRT-5), "Plan B" (planned, no dates, empty) and "Now"
/// (active, with WRT-6 waiting, WRT-7 in progress and WRT-8 done).
/// </summary>
public sealed record SprintSet(WriteWorld W, FakeSprint PlanA, FakeSprint PlanB, FakeSprint Now)
{
    public async Task<Sprint> RowAsync(string name)
    {
        await using var db = W.Api.Db();
        return await db.Sprints.AsNoTracking().SingleAsync(s => s.ProjectId == W.Project && s.Name == name);
    }

    public async Task<Guid> IdAsync(string name) => (await RowAsync(name)).Id;

    public async Task<Guid?> SprintOfAsync(string key) => (await W.RowAsync(key)).SprintId;

    /// <summary>A sprint that exists only here (made before the project was mirrored, say), planned, in the mirrored project.</summary>
    public async Task<Guid> LocalSprintAsync(string name)
    {
        var now = DateTimeOffset.UtcNow;
        var sprint = new Sprint
        {
            WorkspaceId = W.World.WorkspaceId,
            ProjectId = W.Project,
            Number = 99,
            Name = name,
            CreatedBy = W.World.Admin.Id,
            UpdatedBy = W.World.Admin.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var db = W.Api.Db();
        db.Sprints.Add(sprint);
        await db.SaveChangesAsync();
        return sprint.Id;
    }

    /// <summary>An issue that exists only here, in the mirrored project.</summary>
    public async Task<Guid> LocalIssueAsync(string title)
    {
        var now = DateTimeOffset.UtcNow;
        var issue = new Issue
        {
            WorkspaceId = W.World.WorkspaceId,
            ProjectId = W.Project,
            Number = 98,
            Title = title,
            TitleSearch = title.ToLowerInvariant(),
            Position = 98000,
            CreatedBy = W.World.Admin.Id,
            UpdatedBy = W.World.Admin.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var db = W.Api.Db();
        db.Issues.Add(issue);
        await db.SaveChangesAsync();
        return issue.Id;
    }
}

public static class JiraWriteSprintKit
{
    public static async Task<SprintSet> OpenAsync(ApiFactory api, bool linkMember = true, int extraIssues = 0)
    {
        FakeSprint? planA = null, planB = null, now = null;
        var w = await JiraWriteKit.OpenAsync(api, (site, space) =>
        {
            planA = site.Sprint(space, "Plan A", "future", "2026-11-02", "2026-11-13");
            planA.Goal = "Plan goal";
            planB = site.Sprint(space, "Plan B", "future");
            now = site.Sprint(space, "Now", "active", "2026-10-15", "2026-10-28");
            space.Issue("In plan A", i => i.SprintId = planA.Id);
            space.Issue("Waiting in now", i => i.SprintId = now.Id);
            space.Issue("Working in now", i => { i.SprintId = now.Id; i.Status = "In Progress"; i.Category = "indeterminate"; });
            space.Issue("Done in now", i => { i.SprintId = now.Id; i.Status = "Done"; i.Category = "done"; });
            for (var n = 0; n < extraIssues; n++) space.Issue($"Extra {n}");
        }, linkMember);
        await JiraKit.ImportSprintsAsync(w.World, JiraWriteKit.Key, planA!.Id, planB!.Id, now!.Id);
        return new SprintSet(w, planA, planB, now);
    }

    public static string Route(FakeSprint sprint, string tail = "") => $"/rest/agile/1.0/sprint/{sprint.Id}{tail}";

    /// <summary>The sprint form as the web sends it back, with some fields changed (a PUT replaces the definition).</summary>
    public static async Task<HttpResponseMessage> EditAsync(TestUser who, Guid sprint, object? changes = null)
    {
        var view = await who.GetJsonAsync($"/api/sprints/{sprint}");
        var body = new Dictionary<string, object?>();
        foreach (var name in new[] { "name", "goal", "startDate", "endDate", "capacity", "releaseId" }) body[name] = view.GetProperty("sprint").GetProperty(name);
        body["teamUserIds"] = view.GetProperty("team").EnumerateArray().Select(m => m.GetProperty("userId").GetGuid()).ToList();
        return await who.SendAsync(HttpMethod.Put, $"/api/sprints/{sprint}", IssueKit.Merge(body, changes));
    }

    public static async Task<JsonElement> ViewAsync(TestUser who, Guid sprint) => (await who.GetJsonAsync($"/api/sprints/{sprint}")).GetProperty("sprint");
}
