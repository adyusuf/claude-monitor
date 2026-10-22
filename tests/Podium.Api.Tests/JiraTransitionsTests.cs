using System.Text.Json;
using Podium.Api.Data;
using Podium.Api.Integrations;

namespace Podium.Api.Tests;

/// <summary>Which step of a Jira workflow a Podium status asks for (ADR-0015 §7).</summary>
public sealed class JiraTransitionsTests
{
    private const string Workflow = """
        [
          {"id":"11","name":"Start","to":{"name":"In Progress","statusCategory":{"key":"indeterminate"}}},
          {"id":"12","name":"Send to review","to":{"name":"In Review","statusCategory":{"key":"indeterminate"}}},
          {"id":"13","name":"Finish","to":{"name":"Done","statusCategory":{"key":"done"}}},
          {"id":"14","name":"Reject","to":{"name":"Won't Do","statusCategory":{"key":"done"}}},
          {"id":"15","name":"Reopen","to":{"name":"To Do","statusCategory":{"key":"new"}}}
        ]
        """;

    private static string? Pick(string json, string status)
    {
        using var doc = JsonDocument.Parse(json);
        return JiraTransitions.Pick(doc.RootElement, status);
    }

    [Theory]
    [InlineData(IssueStatuses.InProgress, "11")]
    [InlineData(IssueStatuses.InReview, "12")]
    [InlineData(IssueStatuses.Done, "13")]
    [InlineData(IssueStatuses.Cancelled, "14")]
    [InlineData(IssueStatuses.Backlog, "15")]
    [InlineData(IssueStatuses.Ready, "15")]
    public void Each_status_finds_its_own_step(string status, string expected) => Assert.Equal(expected, Pick(Workflow, status));

    [Fact]
    public void Done_does_not_pick_the_rejecting_step_and_progress_does_not_pick_review_when_it_has_a_choice()
    {
        const string onlyDoneLike = """[{"id":"1","name":"x","to":{"name":"Won't Do","statusCategory":{"key":"done"}}},{"id":"2","name":"y","to":{"name":"Shipped","statusCategory":{"key":"done"}}}]""";
        const string reviewFirst = """[{"id":"7","name":"x","to":{"name":"Code Review","statusCategory":{"key":"indeterminate"}}},{"id":"8","name":"y","to":{"name":"Working","statusCategory":{"key":"indeterminate"}}}]""";

        Assert.Equal("2", Pick(onlyDoneLike, IssueStatuses.Done));
        Assert.Equal("8", Pick(reviewFirst, IssueStatuses.InProgress));
    }

    [Fact]
    public void A_status_the_workflow_cannot_reach_finds_nothing()
    {
        const string onlyNew = """[{"id":"15","name":"Reopen","to":{"name":"To Do","statusCategory":{"key":"new"}}}]""";

        Assert.Null(Pick(onlyNew, IssueStatuses.Done));
        Assert.Null(Pick("[]", IssueStatuses.InProgress));
        Assert.Null(Pick("""{"not":"an array"}""", IssueStatuses.Done));
    }

    [Fact]
    public void A_step_without_a_target_is_ignored_and_the_first_of_the_category_is_the_fallback()
    {
        const string odd = """[{"id":"1","name":"broken"},{"id":"2","name":"y","to":{"name":"Something","statusCategory":{"key":"done"}}}]""";

        Assert.Equal("2", Pick(odd, IssueStatuses.Done));
    }

    [Theory]
    [InlineData(IssueStatuses.Backlog, "new")]
    [InlineData(IssueStatuses.Ready, "new")]
    [InlineData(IssueStatuses.InProgress, "indeterminate")]
    [InlineData(IssueStatuses.InReview, "indeterminate")]
    [InlineData(IssueStatuses.Done, "done")]
    [InlineData(IssueStatuses.Cancelled, "done")]
    [InlineData("unknown", "new")]
    public void Status_categories(string status, string category) => Assert.Equal(category, JiraTransitions.Category(status));

    [Theory]
    [InlineData(WorkItemTypes.Epic, "Epic")]
    [InlineData(WorkItemTypes.Story, "Story")]
    [InlineData(WorkItemTypes.Bug, "Bug")]
    [InlineData(WorkItemTypes.Task, "Task")]
    [InlineData(WorkItemTypes.Spike, "Task")]
    public void Type_names_for_jira(string type, string name) => Assert.Equal(name, JiraWriter.TypeName(type));

    [Theory]
    [InlineData("1", "Highest")]
    [InlineData("3", "Medium")]
    [InlineData("5", "Lowest")]
    [InlineData("9", null)]
    [InlineData(null, null)]
    public void Priority_names_for_jira(string? rank, string? name) => Assert.Equal(name, JiraWriter.PriorityName(rank));
}
