using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The people that come with an imported space (ADR-0015 §1 decision 4): who is matched to a member, who is assigned, who leads.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationImportPeopleTests(ApiFactory api)
{
    private sealed record Crowd(JiraWorld World, TestUser Member, TestUser Viewer, FakePerson ByMail, FakePerson Viewing, FakePerson Hidden, FakePerson Outsider, FakePerson Inactive, FakeSpace Space);

    /// <summary>A connected site with a space whose people are: a member (e-mail in capitals), a viewer, one with no e-mail, one who is no member, and one inactive.</summary>
    private async Task<Crowd> CrowdAsync(string key = "PPL")
    {
        var w = await JiraKit.ConnectedAsync(api);
        var member = await RemoteKit.MemberAsync(api, w.Admin, "ipm-member");
        var viewer = await RemoteKit.MemberAsync(api, w.Admin, "ipm-viewer", "viewer");
        var byMail = new FakePerson("acc-member", "Member Person", member.Email.ToUpperInvariant());
        var viewing = new FakePerson("acc-viewer", "Viewer Person", viewer.Email);
        var hidden = new FakePerson("acc-hidden", "Hidden Person");
        var outsider = new FakePerson("acc-outsider", "Outside Person", Emails.New("ipm-outside"));
        var inactive = new FakePerson("acc-inactive", "Gone Person", Emails.New("ipm-gone")) { Active = false };
        var space = w.Site.Space(key, "People space");
        space.People.AddRange([byMail, viewing, hidden, outsider, inactive]);
        return new Crowd(w, member, viewer, byMail, viewing, hidden, outsider, inactive, space);
    }

    [Fact]
    public async Task The_people_of_the_space_become_rows_and_are_matched_to_members_by_e_mail_without_regard_to_case()
    {
        var c = await CrowdAsync();
        c.Space.Issue("Reported by an outsider", i => i.Reporter = c.Outsider);

        await JiraKit.ImportAsync(c.World, "PPL");

        await using var db = api.Db();
        var people = await db.IntegrationPeople.AsNoTracking().Where(p => p.ConnectionId == c.World.ConnectionId).ToDictionaryAsync(p => p.ExternalAccountId);
        Assert.Equal(["acc-hidden", "acc-inactive", "acc-member", "acc-outsider", "acc-viewer"], people.Keys.Order());
        Assert.Equal(c.Member.Id, people["acc-member"].UserId);
        Assert.Equal(c.Member.Email.ToLowerInvariant(), people["acc-member"].EmailNormalized);
        Assert.Equal(c.Viewer.Id, people["acc-viewer"].UserId); // a viewer is a member too
        Assert.Null(people["acc-outsider"].UserId);
        Assert.Null(people["acc-hidden"].Email);
        Assert.Null(people["acc-hidden"].EmailNormalized);
        Assert.False(people["acc-inactive"].Active);
        Assert.True(people["acc-hidden"].Active);
        Assert.All(people.Values, p => Assert.Null(p.InvitedAt)); // the switch is off
        var answer = (await c.World.Admin.GetJsonAsync($"{c.World.Base}/people")).EnumerateArray().ToList();
        Assert.Equal(["Gone Person", "Hidden Person", "Member Person", "Outside Person", "Viewer Person"], answer.Select(p => p.GetProperty("displayName").GetString()));
        Assert.Equal(c.Member.Id, answer.Single(p => p.GetProperty("displayName").GetString() == "Member Person").GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task An_issue_assigned_to_a_member_gets_the_member_and_every_assigned_person_gets_the_person_row()
    {
        var c = await CrowdAsync();
        c.Space.Issue("Member's", i => i.Assignee = c.ByMail);
        c.Space.Issue("Hidden's", i => i.Assignee = c.Hidden);
        c.Space.Issue("Outsider's", i => i.Assignee = c.Outsider);
        c.Space.Issue("Nobody's");

        await JiraKit.ImportAsync(c.World, "PPL");

        var project = await JiraKit.ProjectIdAsync(c.World, "PPL");
        var issues = (await JiraKit.IssuesAsync(c.World, project)).ToDictionary(i => i.Title);
        await using var db = api.Db();
        var person = await db.IntegrationPeople.AsNoTracking().Where(p => p.ConnectionId == c.World.ConnectionId).ToDictionaryAsync(p => p.ExternalAccountId, p => p.Id);
        Assert.Equal((c.Member.Id, person["acc-member"]), (issues["Member's"].AssigneeUserId, issues["Member's"].AssigneePersonId));
        Assert.Equal((null, person["acc-hidden"]), (issues["Hidden's"].AssigneeUserId, issues["Hidden's"].AssigneePersonId)); // no e-mail: the person only
        Assert.Equal((null, person["acc-outsider"]), (issues["Outsider's"].AssigneeUserId, issues["Outsider's"].AssigneePersonId));
        Assert.Equal((null, null), (issues["Nobody's"].AssigneeUserId, issues["Nobody's"].AssigneePersonId));
        var shown = (await c.World.Admin.GetJsonAsync($"/api/projects/{project}/issues")).GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("title").GetString()!, i => i.GetProperty("assigneeName").GetString());
        Assert.Equal("Hidden Person", shown["Hidden's"]); // a person who is no member shows by name
        Assert.Equal("Outside Person", shown["Outsider's"]);
        Assert.Equal("Test Person", shown["Member's"]);
        Assert.Null(shown["Nobody's"]);
    }

    [Fact]
    public async Task An_issue_assigned_to_a_workspace_viewer_gets_the_person_row_only()
    {
        var c = await CrowdAsync();
        c.Space.Issue("Viewer's", i => i.Assignee = c.Viewing);

        await JiraKit.ImportAsync(c.World, "PPL");

        var issue = (await JiraKit.IssuesAsync(c.World, await JiraKit.ProjectIdAsync(c.World, "PPL"))).Single();
        Assert.Null(issue.AssigneeUserId);
        Assert.NotNull(issue.AssigneePersonId);
    }

    [Fact]
    public async Task A_project_lead_who_is_a_member_becomes_a_project_manager_and_nobody_else_does()
    {
        var c = await CrowdAsync();
        c.Space.Lead = c.ByMail;
        var viewerLed = c.World.Site.Space("LDV");
        viewerLed.People.Add(c.Viewing);
        viewerLed.Lead = c.Viewing;
        var outsiderLed = c.World.Site.Space("LDX");
        outsiderLed.Lead = c.Outsider;
        var ownerLed = c.World.Site.Space("LDO");
        ownerLed.Lead = new FakePerson("acc-owner", "Owner Person", c.World.Admin.Email);

        await JiraKit.ImportAsync(c.World, "PPL", "LDV", "LDX", "LDO");

        await using var db = api.Db();
        async Task<List<(Guid, string)>> Managers(string key)
        {
            var project = await JiraKit.ProjectIdAsync(c.World, key);
            return (await db.ProjectMembers.AsNoTracking().Where(m => m.ProjectId == project).ToListAsync()).Select(m => (m.UserId, m.Role)).ToList();
        }

        Assert.Equal([(c.Member.Id, ProjectRoles.Manager)], await Managers("PPL"));
        Assert.Empty(await Managers("LDV")); // a viewer cannot manage a project
        Assert.Empty(await Managers("LDX")); // not a member
        Assert.Empty(await Managers("LDO")); // the owner of the project needs no role
        // A lead who already has a role on the project keeps it when the space is imported again.
        var ppl = await JiraKit.ProjectIdAsync(c.World, "PPL");
        (await c.World.Admin.SendAsync(HttpMethod.Put, $"/api/projects/{ppl}/members/{c.Member.Id}", new { role = "viewer" })).EnsureSuccessStatusCode();
        await JiraKit.ImportAsync(c.World, "PPL");
        Assert.Equal([(c.Member.Id, ProjectRoles.Viewer)], await Managers("PPL"));
    }
}
