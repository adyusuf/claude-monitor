using System.Net;
using ClaudeMonitor.Api.Integrations;
using ClaudeMonitor.Api.Tests.Infrastructure;

namespace ClaudeMonitor.Api.Tests;

/// <summary>The Jira write adapter for workflow steps, new issues, rank and sprint membership (ADR-0015 §7).</summary>
public sealed class JiraWriterFlowTests : IDisposable
{
    private readonly JiraClients stack = new();
    private readonly FakeSite site;
    private readonly FakeSpace space;
    private readonly TrackerContext ctx;

    public JiraWriterFlowTests()
    {
        site = stack.Jira.Site();
        space = site.Space("ABC");
        ctx = stack.Context(site, stack.Jira.Account("Writer", null, site));
    }

    public void Dispose() => stack.Dispose();

    [Theory]
    [InlineData("in_progress", "t21")]
    [InlineData("in_review", "t22")]
    [InlineData("done", "t31")]
    [InlineData("cancelled", "t32")]
    public async Task A_status_is_the_workflow_step_that_fits_it_asked_for_first_and_then_taken(string status, string id)
    {
        var issue = space.Issue("Moving");

        Assert.True(await stack.Writer.TransitionItemAsync(ctx, issue.Key, status, CancellationToken.None));

        Assert.Equal(["GET /rest/api/3/issue/ABC-1/transitions", "POST /rest/api/3/issue/ABC-1/transitions"], stack.Jira.Calls(site));
        Assert.Equal(id, stack.Jira.Sent(site, HttpMethod.Post, "/rest/api/3/issue/ABC-1/transitions")["transition"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_status_with_no_fitting_step_answers_false_and_posts_nothing()
    {
        var issue = space.Issue("Stuck");
        site.Workflow.RemoveAll(s => s.Category == "done");
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var odd = new JiraClients(() => stub);

        Assert.False(await stack.Writer.TransitionItemAsync(ctx, issue.Key, "done", CancellationToken.None));
        Assert.False(await stack.Writer.TransitionItemAsync(ctx, issue.Key, "cancelled", CancellationToken.None));
        Assert.False(await odd.Writer.TransitionItemAsync(ctx, issue.Key, "done", CancellationToken.None)); // an answer with no list of steps

        Assert.Empty(stack.Jira.Writes(site));
        Assert.Equal([HttpMethod.Get], stub.Seen.Select(r => r.Method).Distinct());
    }

    [Theory]
    [InlineData("epic", "Epic")]
    [InlineData("story", "Story")]
    [InlineData("bug", "Bug")]
    [InlineData("task", "Task")]
    [InlineData("spike", "Task")]
    [InlineData("something_new", "Task")]
    public async Task A_new_issue_names_its_project_and_the_jira_type_and_comes_back_with_its_id_and_key(string type, string name)
    {
        var (id, key) = await stack.Writer.CreateItemAsync(ctx, new NewItem("ABC", type, new ItemChange { Summary = "Fresh" }), CancellationToken.None);

        var fields = stack.Jira.Sent(site, HttpMethod.Post, "/rest/api/3/issue")["fields"]!.AsObject();
        Assert.Equal(["issuetype", "project", "summary"], fields.Select(f => f.Key).Order(StringComparer.Ordinal));
        Assert.Equal(("ABC", name, "Fresh"), (fields["project"]!["key"]!.GetValue<string>(), fields["issuetype"]!["name"]!.GetValue<string>(), fields["summary"]!.GetValue<string>()));
        Assert.Equal(("ABC-1", space.Issues[0].Id, name), (key, id, space.Issues[0].Type));
        Assert.Equal(name, JiraWriter.TypeName(type));
    }

    [Fact]
    public async Task A_new_issue_carries_the_same_fields_as_an_update_and_a_site_that_answers_without_an_id_or_key_is_rejected()
    {
        await stack.Writer.CreateItemAsync(ctx, new NewItem("ABC", "bug", new ItemChange
        {
            Summary = "Full",
            SetDescription = true,
            Description = "details",
            SetPriority = true,
            PriorityRank = "2",
            SetEstimate = true,
            Estimate = "3",
            SetAssignee = true,
            AssigneeAccountId = "acc-2",
            SetParent = true,
            ParentKey = "ABC-7",
        }), CancellationToken.None);

        var fields = stack.Jira.Sent(site, HttpMethod.Post, "/rest/api/3/issue")["fields"]!.AsObject();
        Assert.Equal(["assignee", "customfield_10016", "description", "issuetype", "parent", "priority", "project", "summary"], fields.Select(f => f.Key).Order(StringComparer.Ordinal));
        Assert.Equal(("High", "acc-2", "ABC-7", 3), (fields["priority"]!["name"]!.GetValue<string>(), fields["assignee"]!["accountId"]!.GetValue<string>(), fields["parent"]!["key"]!.GetValue<string>(), fields["customfield_10016"]!.GetValue<int>()));
        foreach (var answer in new[] { """{"id":"1"}""", """{"key":"ABC-1"}""", "{}" })
        {
            using var odd = new JiraClients(() => new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(answer) }));
            var e = await Assert.ThrowsAsync<TrackerException>(() => odd.Writer.CreateItemAsync(ctx, new NewItem("ABC", "story", new ItemChange { Summary = "x" }), CancellationToken.None));
            Assert.Equal(TrackerException.Codes.Rejected, e.Code);
        }
    }

    [Fact]
    public async Task Rank_goes_after_an_issue_when_one_is_given_and_before_another_when_not()
    {
        space.Issue("One");
        space.Issue("Two");
        space.Issue("Three");

        await stack.Writer.RankItemAsync(ctx, "ABC-3", "ABC-1", null, CancellationToken.None);
        await stack.Writer.RankItemAsync(ctx, "ABC-2", null, "ABC-3", CancellationToken.None);
        await stack.Writer.RankItemAsync(ctx, "ABC-1", "ABC-2", "ABC-3", CancellationToken.None);

        var bodies = stack.Jira.Bodies(site, HttpMethod.Put, "/rest/agile/1.0/issue/rank");
        Assert.Equal(("ABC-3", "ABC-1", false), (bodies[0]["issues"]![0]!.GetValue<string>(), bodies[0]["rankAfterIssue"]!.GetValue<string>(), bodies[0].ContainsKey("rankBeforeIssue")));
        Assert.Equal(("ABC-2", "ABC-3", false), (bodies[1]["issues"]![0]!.GetValue<string>(), bodies[1]["rankBeforeIssue"]!.GetValue<string>(), bodies[1].ContainsKey("rankAfterIssue")));
        Assert.Equal(("ABC-2", false), (bodies[2]["rankAfterIssue"]!.GetValue<string>(), bodies[2].ContainsKey("rankBeforeIssue"))); // after wins when both are given
    }

    [Fact]
    public async Task Issues_go_into_a_sprint_in_calls_of_at_most_fifty_and_none_means_no_call()
    {
        var sprint = site.Sprint(space, "Target", "future");
        var keys = Enumerable.Range(0, 120).Select(n => space.Issue($"Issue {n}").Key).ToList();

        await stack.Writer.AddToIterationAsync(ctx, sprint.IdText, keys, CancellationToken.None);
        await stack.Writer.AddToIterationAsync(ctx, sprint.IdText, [], CancellationToken.None);

        var bodies = stack.Jira.Bodies(site, HttpMethod.Post, $"/rest/agile/1.0/sprint/{sprint.Id}/issue");
        Assert.Equal([50, 50, 20], bodies.Select(b => b["issues"]!.AsArray().Count));
        Assert.Equal(keys, bodies.SelectMany(b => b["issues"]!.AsArray().Select(k => k!.GetValue<string>())));
        Assert.Equal(120, space.Issues.Count(i => i.SprintId == sprint.Id));
    }

    [Fact]
    public async Task Issues_go_back_to_the_backlog_in_calls_of_at_most_fifty()
    {
        var sprint = site.Sprint(space, "Source", "future");
        var keys = Enumerable.Range(0, 51).Select(n => space.Issue($"Issue {n}", i => i.SprintId = sprint.Id).Key).ToList();

        await stack.Writer.MoveToBacklogAsync(ctx, keys, CancellationToken.None);
        await stack.Writer.MoveToBacklogAsync(ctx, [], CancellationToken.None);

        Assert.Equal([50, 1], stack.Jira.Bodies(site, HttpMethod.Post, "/rest/agile/1.0/backlog/issue").Select(b => b["issues"]!.AsArray().Count));
        Assert.All(space.Issues, i => Assert.Null(i.SprintId));
    }
}
