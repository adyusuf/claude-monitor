using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Podium.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>
/// A connected site with an imported space "WRT" and a member who has linked their own Atlassian account (ADR-0015 §7). The space holds an epic
/// (WRT-1), two stories (WRT-2 with a description, a priority and three points, and WRT-3) and a story that Jira assigned to a person who is no member (WRT-4).
/// </summary>
public sealed record WriteWorld(JiraWorld World, FakeSpace Space, TestUser Member, FakeAccount? MemberAccount, Guid Project, FakePerson Outsider)
{
    public ApiFactory Api => World.Api;
    public FakeSite Site => World.Site;
    public FakeJira Jira => World.Api.Jira;

    public Task<Issue> RowAsync(string key) => JiraKit.IssueAsync(World, key);

    public async Task<Guid> IdAsync(string key) => (await RowAsync(key)).Id;

    /// <summary>The Jira side of the link of a local row.</summary>
    public async Task<IntegrationLink> LinkAsync(Guid local, string kind = LinkKinds.Issue)
    {
        await using var db = Api.Db();
        return await db.IntegrationLinks.AsNoTracking().SingleAsync(l => l.ConnectionId == World.ConnectionId && l.Kind == kind && l.LocalId == local);
    }

    /// <summary>A change that Jira's side made after the last sync: the issue's <c>updated</c> moves on.</summary>
    public void ChangeInJira(string key, Action<FakeIssue> change)
    {
        var issue = Site.Find(key)!;
        change(issue);
        Jira.Touch(issue);
    }
}

public static class JiraWriteKit
{
    public const string Key = "WRT";

    /// <summary>Why the tests that send a sprint date are skipped: the product throws before the call (JiraWriter.cs, <c>Stamp</c>).</summary>

    public static async Task<WriteWorld> OpenAsync(ApiFactory api, Action<FakeSite, FakeSpace>? seed = null, bool linkMember = true, string role = "member")
    {
        FakeSpace? space = null;
        var outsider = new FakePerson("p-outsider", "Jira Outsider");
        var w = await JiraKit.ConnectedAsync(api, site =>
        {
            space = site.Space(Key, "Write space");
            space.People.Add(outsider);
            space.Issue("Epic one", i => i.Type = "Epic");
            space.Issue("Story one", i => { i.Description = "old text"; i.Priority = "Medium"; i.Points = 3; });
            space.Issue("Story two");
            space.Issue("Story four", i => i.Assignee = outsider);
            seed?.Invoke(site, space);
        });
        await JiraKit.ImportAsync(w, Key);
        var project = await JiraKit.ProjectIdAsync(w, Key);
        var member = await RemoteKit.MemberAsync(api, w.Admin, "wr-member", role);
        (await w.Admin.SendAsync(HttpMethod.Put, $"/api/projects/{project}/members/{member.Id}", new { role = "contributor" })).EnsureSuccessStatusCode();
        var account = linkMember ? await LinkAccountAsync(member, w) : null;
        return new WriteWorld(w, space!, member, account, project, outsider);
    }

    /// <summary>The person links their own Atlassian account to the connection (the browser round trip through the fake Atlassian).</summary>
    public static async Task<FakeAccount> LinkAccountAsync(TestUser user, JiraWorld w)
    {
        var response = await user.PostAsync($"{w.Base}/account/connect");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var url = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizeUrl").GetString()!;
        var account = w.Api.Jira.Account("Own " + user.Email[..8], user.Email, w.Site);
        Assert.Equal("ok", JiraKit.ResultOf(await JiraKit.ApproveAsync(user, w.Api.Jira, account, url)));
        return account;
    }

    /// <summary>The whole issue form as the web sends it back, with some fields changed: a PUT replaces the issue, so the rest is sent as it is.</summary>
    public static async Task<HttpResponseMessage> EditAsync(TestUser who, Guid issue, object? changes = null)
    {
        var view = await who.GetJsonAsync($"/api/issues/{issue}");
        var body = new Dictionary<string, object?>();
        foreach (var name in new[] { "title", "type", "priority", "estimate", "status", "assigneeUserId", "parentId", "applicationId", "componentId", "releaseId" })
        {
            body[name] = view.GetProperty("issue").GetProperty(name);
        }

        foreach (var name in new[] { "description", "acceptanceCriteria", "technicalDesign", "bugEnvironment", "reproSteps", "expectedResult", "actualResult", "severity" })
        {
            body[name] = view.GetProperty(name);
        }

        return await who.SendAsync(HttpMethod.Put, $"/api/issues/{issue}", IssueKit.Merge(body, changes));
    }

    /// <summary>The problem title of an error answer (the short code the web translates).</summary>
    public static async Task<string?> TitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();

    /// <summary>Asserts the status and the short code of an error answer, and that nothing of the tool's own text reached the answer.</summary>
    public static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string title)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(title, JsonDocument.Parse(text).RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain("secret detail", text, StringComparison.Ordinal);
    }
}
