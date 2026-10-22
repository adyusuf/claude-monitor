using System.Net;
using System.Text.Json.Nodes;
using Podium.Api.Integrations;
using Podium.Api.Tests.Infrastructure;

namespace Podium.Api.Tests;

/// <summary>The Jira write adapter for issues (ADR-0015 §7): exactly what goes on the wire for each kind of change, over the scripted site and over odd answers.</summary>
public sealed class JiraWriterItemTests : IDisposable
{
    private readonly JiraClients stack = new();
    private readonly FakeSite site;
    private readonly FakeSpace space;
    private readonly FakeIssue issue;
    private readonly TrackerContext ctx;

    public JiraWriterItemTests()
    {
        site = stack.Jira.Site();
        space = site.Space("ABC");
        issue = space.Issue("Old title", i => { i.Description = "old"; i.Priority = "Medium"; });
        ctx = stack.Context(site, stack.Jira.Account("Writer", null, site));
    }

    public void Dispose() => stack.Dispose();

    private string Route => "/rest/api/3/issue/" + issue.Key;

    private JsonObject Fields() => stack.Jira.Sent(site, HttpMethod.Put, Route)["fields"]!.AsObject();

    private Task Update(ItemChange change) => stack.Writer.UpdateItemAsync(ctx, issue.Key, change, CancellationToken.None);

    [Fact]
    public async Task The_stamp_is_the_issues_updated_value_and_an_answer_without_fields_has_none()
    {
        Assert.Equal(FakeJira.Stamp(issue.Updated), await stack.Writer.ItemStampAsync(ctx, issue.Key, CancellationToken.None));
        using var odd = new JiraClients(() => new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));

        Assert.Null(await odd.Writer.ItemStampAsync(ctx, issue.Key, CancellationToken.None));
        var gone = await Assert.ThrowsAsync<TrackerException>(() => stack.Writer.ItemStampAsync(ctx, "ABC-404", CancellationToken.None));
        Assert.Equal(TrackerException.Codes.NotFound, gone.Code);
    }

    [Fact]
    public async Task A_change_with_only_a_summary_sends_only_the_summary_and_an_empty_change_sends_nothing()
    {
        await Update(new ItemChange());
        Assert.Empty(stack.Jira.Writes(site));

        await Update(new ItemChange { Summary = "New title" });

        Assert.Equal(["summary"], Fields().Select(f => f.Key));
        Assert.Equal("New title", issue.Summary);
        await Assert.ThrowsAsync<ArgumentNullException>(() => stack.Writer.UpdateItemAsync(ctx, issue.Key, null!, CancellationToken.None));
    }

    [Fact]
    public async Task A_description_goes_as_an_atlassian_document_one_paragraph_per_line_and_a_blank_one_clears_it()
    {
        await Update(new ItemChange { SetDescription = true, Description = "first\r\n\r\nthird" });

        var doc = Fields()["description"]!.AsObject();
        var paragraphs = doc["content"]!.AsArray();
        Assert.Equal(("doc", 1, 3), (doc["type"]!.GetValue<string>(), doc["version"]!.GetValue<int>(), paragraphs.Count));
        Assert.Equal(("first", null, "third"), (Text(paragraphs[0]), Text(paragraphs[1]), Text(paragraphs[2])));
        Assert.Equal("first\n\nthird", issue.Description);

        await Update(new ItemChange { SetDescription = true, Description = "  " });
        await Update(new ItemChange { SetDescription = true, Description = null });

        var bodies = stack.Jira.Bodies(site, HttpMethod.Put, Route);
        Assert.All(bodies.Skip(1), b => Assert.True(b["fields"]!.AsObject().ContainsKey("description") && b["fields"]!["description"] is null));
        Assert.Null(issue.Description);
    }

    private static string? Text(JsonNode? paragraph) => paragraph!["content"]?[0]?["text"]?.GetValue<string>();

    [Theory]
    [InlineData("1", "Highest")]
    [InlineData("2", "High")]
    [InlineData("3", "Medium")]
    [InlineData("4", "Low")]
    [InlineData("5", "Lowest")]
    public async Task The_one_to_five_scale_maps_to_jiras_priority_names(string rank, string name)
    {
        await Update(new ItemChange { SetPriority = true, PriorityRank = rank });

        Assert.Equal(name, Fields()["priority"]!["name"]!.GetValue<string>());
        Assert.Equal(name, JiraWriter.PriorityName(rank));
        Assert.Equal(name, issue.Priority);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("must")]
    [InlineData("6")]
    public async Task A_priority_that_is_not_on_the_scale_or_is_cleared_sends_null(string? rank)
    {
        await Update(new ItemChange { SetPriority = true, PriorityRank = rank });

        Assert.True(Fields().ContainsKey("priority") && Fields()["priority"] is null);
        Assert.Null(issue.Priority);
    }

    [Fact]
    public async Task The_assignee_goes_as_an_account_id_and_null_unassigns()
    {
        await Update(new ItemChange { SetAssignee = true, AssigneeAccountId = "acc-1" });
        await Update(new ItemChange { SetAssignee = true, AssigneeAccountId = null });

        var bodies = stack.Jira.Bodies(site, HttpMethod.Put, Route);
        Assert.Equal("acc-1", bodies[0]["fields"]!["assignee"]!["accountId"]!.GetValue<string>());
        Assert.True(bodies[1]["fields"]!.AsObject().ContainsKey("assignee") && bodies[1]["fields"]!["assignee"] is null);
        Assert.Null(issue.Assignee);
    }

    [Fact]
    public async Task The_parent_epic_goes_as_a_key_and_null_takes_it_off()
    {
        await Update(new ItemChange { SetParent = true, ParentKey = "ABC-9" });
        await Update(new ItemChange { SetParent = true, ParentKey = null });

        var bodies = stack.Jira.Bodies(site, HttpMethod.Put, Route);
        Assert.Equal("ABC-9", bodies[0]["fields"]!["parent"]!["key"]!.GetValue<string>());
        Assert.True(bodies[1]["fields"]!.AsObject().ContainsKey("parent") && bodies[1]["fields"]!["parent"] is null);
        Assert.Null(issue.ParentKey);
    }

    [Theory]
    [InlineData("5", 5.0)]
    [InlineData("2.5", 2.5)]
    [InlineData("0", 0.0)]
    public async Task The_estimate_goes_to_the_sites_story_point_field_as_a_number(string estimate, double points)
    {
        await Update(new ItemChange { SetEstimate = true, Estimate = estimate });

        Assert.Equal(points, Fields()["customfield_10016"]!.GetValue<double>());
        Assert.Equal(points, issue.Points);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("L")]
    public async Task An_estimate_that_is_no_number_clears_the_story_points_and_the_sites_fields_are_read_once(string? estimate)
    {
        await Update(new ItemChange { SetEstimate = true, Estimate = "3" });
        await Update(new ItemChange { SetEstimate = true, Estimate = estimate });

        Assert.True(stack.Jira.Bodies(site, HttpMethod.Put, Route)[1]["fields"]!.AsObject().ContainsKey("customfield_10016"));
        Assert.Null(issue.Points);
        Assert.Equal(1, stack.Jira.Calls(site).Count(c => c == "GET /rest/api/3/field"));
    }

    [Fact]
    public async Task A_site_without_a_story_point_field_rejects_an_estimate_and_sends_nothing()
    {
        using var bare = new JiraClients(() => new StubHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/rest/api/3/field", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""[{"id":"summary","name":"Summary","schema":{"type":"string"}}]""") }
            : new HttpResponseMessage(HttpStatusCode.NoContent)));

        var refused = await Assert.ThrowsAsync<TrackerException>(() => bare.Writer.UpdateItemAsync(ctx, "ABC-1", new ItemChange { Summary = "x", SetEstimate = true, Estimate = "3" }, CancellationToken.None));
        var create = await Assert.ThrowsAsync<TrackerException>(() => bare.Writer.CreateItemAsync(ctx, new NewItem("ABC", "story", new ItemChange { Summary = "x", SetEstimate = true, Estimate = "3" }), CancellationToken.None));

        Assert.Equal((TrackerException.Codes.Rejected, TrackerException.Codes.Rejected), (refused.Code, create.Code));
    }
}
