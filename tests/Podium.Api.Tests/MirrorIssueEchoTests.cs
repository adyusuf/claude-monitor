using System.Net;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>What Jira answers to our own write is stored (its new <c>updated</c>), so the echo changes nothing; and who an issue is assigned to (ADR-0015 §7, §8a).</summary>
[Collection(ApiGroup.Name)]
public sealed class MirrorIssueEchoTests(ApiFactory api)
{
    private static (int Created, int Updated) Counts(System.Text.Json.JsonElement answer) => (answer[0].GetProperty("created").GetInt32(), answer[0].GetProperty("updated").GetInt32());

    [Fact]
    public async Task After_a_write_the_link_holds_jiras_new_stamp_and_the_echo_of_our_own_write_changes_nothing()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var id = await w.IdAsync("WRT-2");
        var epic = await w.IdAsync("WRT-1");
        var baseline = Counts(await JiraKit.ImportAsync(w.World, JiraWriteKit.Key)); // a pass over an unchanged space
        var stampBefore = (await w.LinkAsync(id)).ExternalUpdated;

        (await JiraWriteKit.EditAsync(w.Member, id, new { title = "Edited here", priority = "2", estimate = "13", description = "text here", parentId = epic })).EnsureSuccessStatusCode();

        var link = await w.LinkAsync(id);
        Assert.NotEqual(stampBefore, link.ExternalUpdated);
        Assert.Equal(FakeJira.Stamp(w.Site.Find("WRT-2")!.Updated), link.ExternalUpdated);
        var echo = Counts(await JiraKit.ImportAsync(w.World, JiraWriteKit.Key));
        Assert.Equal(baseline, echo);
        var row = await w.RowAsync("WRT-2");
        Assert.Equal(("Edited here", "2", "13", "text here", epic), (row.Title, row.Priority, row.Estimate, row.Description, row.ParentId));
    }

    [Fact]
    public async Task Two_edits_in_a_row_both_succeed_because_the_stamp_stays_in_step_with_jira()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var id = await w.IdAsync("WRT-2");

        var first = await JiraWriteKit.EditAsync(w.Member, id, new { title = "First" });
        var second = await JiraWriteKit.EditAsync(w.Member, id, new { title = "Second", status = "done" });
        var third = await JiraWriteKit.EditAsync(w.Member, id, new { title = "Third" });

        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.NoContent], [first.StatusCode, second.StatusCode, third.StatusCode]);
        Assert.Equal(("Third", "Done"), (w.Site.Find("WRT-2")!.Summary, w.Site.Find("WRT-2")!.Status));
    }

    [Fact]
    public async Task An_issue_jira_assigned_to_a_non_member_stays_assigned_when_the_form_sends_nobody_and_nothing_is_sent_for_it()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var before = await w.RowAsync("WRT-4");
        Assert.Equal((null, true), (before.AssigneeUserId, before.AssigneePersonId is not null));

        (await JiraWriteKit.EditAsync(w.Member, before.Id, new { title = "Renamed", assigneeUserId = (Guid?)null })).EnsureSuccessStatusCode();

        var fields = w.Jira.Sent(w.Site, HttpMethod.Put, "/rest/api/3/issue/WRT-4")["fields"]!.AsObject();
        Assert.Equal(["summary"], fields.Select(f => f.Key));
        var after = await w.RowAsync("WRT-4");
        Assert.Equal((before.AssigneePersonId, null), (after.AssigneePersonId, after.AssigneeUserId));
        Assert.Equal("p-outsider", w.Site.Find("WRT-4")!.Assignee!.AccountId);
    }

    [Fact]
    public async Task Assigning_a_member_replaces_the_outsider_and_taking_the_member_off_unassigns_in_jira()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var id = await w.IdAsync("WRT-4");

        (await JiraWriteKit.EditAsync(w.Member, id, new { assigneeUserId = w.Member.Id })).EnsureSuccessStatusCode();

        Assert.Equal(w.MemberAccount!.Id, w.Site.Find("WRT-4")!.Assignee!.AccountId);
        Assert.Equal(w.Member.Id, (await w.RowAsync("WRT-4")).AssigneeUserId);

        (await JiraWriteKit.EditAsync(w.Member, id, new { assigneeUserId = (Guid?)null })).EnsureSuccessStatusCode();

        Assert.Null(w.Site.Find("WRT-4")!.Assignee);
        var row = await w.RowAsync("WRT-4");
        Assert.Equal((null, null), (row.AssigneeUserId, row.AssigneePersonId));
    }

    [Fact]
    public async Task A_member_without_an_account_of_their_own_can_be_assigned_through_the_jira_person_they_are_linked_to_and_one_without_either_cannot()
    {
        var w = await JiraWriteKit.OpenAsync(api);
        var id = await w.IdAsync("WRT-2");
        var third = await RemoteKit.MemberAsync(api, w.World.Admin, "wr-third");
        var lonely = await RemoteKit.MemberAsync(api, w.World.Admin, "wr-lonely");
        await using var db = api.Db();
        var person = await db.IntegrationPeople.AsNoTracking().SingleAsync(p => p.ConnectionId == w.World.ConnectionId && p.ExternalAccountId == "p-outsider");
        (await w.World.Admin.SendAsync(HttpMethod.Put, $"/api/integrations/people/{person.Id}", new { userId = third.Id })).EnsureSuccessStatusCode();

        var mapped = await JiraWriteKit.EditAsync(w.Member, id, new { assigneeUserId = third.Id });
        var writes = w.Jira.Writes(w.Site).Count;
        var nobody = await JiraWriteKit.EditAsync(w.Member, id, new { assigneeUserId = lonely.Id });

        Assert.Equal(HttpStatusCode.NoContent, mapped.StatusCode);
        Assert.Equal("p-outsider", w.Site.Find("WRT-2")!.Assignee!.AccountId);
        var row = await w.RowAsync("WRT-2");
        Assert.Equal((third.Id, person.Id), (row.AssigneeUserId, row.AssigneePersonId));
        Assert.Equal(("assignee_not_in_jira", HttpStatusCode.BadRequest), (await JiraWriteKit.TitleAsync(nobody), nobody.StatusCode));
        Assert.Equal(writes, w.Jira.Writes(w.Site).Count);
        Assert.Equal(third.Id, (await w.RowAsync("WRT-2")).AssigneeUserId);
    }
}
