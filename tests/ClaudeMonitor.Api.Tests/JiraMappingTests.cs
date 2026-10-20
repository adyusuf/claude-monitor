using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Integrations;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Jira's words to Podium's closed sets (ADR-0015 §4), table by table; an unknown word never fails an import.</summary>
public sealed class JiraMappingTests
{
    [Theory]
    [InlineData("Epic", false, WorkItemTypes.Epic)]
    [InlineData("  EPIC ", false, WorkItemTypes.Epic)]
    [InlineData("Story", false, WorkItemTypes.Story)]
    [InlineData("User Story", false, WorkItemTypes.Story)]
    [InlineData("Bug", false, WorkItemTypes.Bug)]
    [InlineData("Defect", false, WorkItemTypes.Bug)]
    [InlineData("Task", false, WorkItemTypes.Task)]
    [InlineData("Technical Spike", false, WorkItemTypes.Spike)]
    [InlineData("Spike", false, WorkItemTypes.Spike)]
    [InlineData("Incident", false, WorkItemTypes.Task)] // unknown word
    [InlineData("", false, WorkItemTypes.Task)]
    [InlineData("Sub-task", true, WorkItemTypes.Task)]
    [InlineData("Bug", true, WorkItemTypes.Task)] // a sub-task is a task whatever it is called
    [InlineData("Epic", true, WorkItemTypes.Task)]
    public void A_type_name_maps_to_one_of_the_five_item_types(string name, bool subtask, string expected) =>
        Assert.Equal(expected, JiraMapping.Type(name, subtask));

    [Theory]
    [InlineData("Highest", "1")]
    [InlineData("Blocker", "1")]
    [InlineData("High", "2")]
    [InlineData("Critical", "2")]
    [InlineData("Major", "2")]
    [InlineData("Medium", "3")]
    [InlineData("Normal", "3")]
    [InlineData("Low", "4")]
    [InlineData("Minor", "4")]
    [InlineData("Lowest", "5")]
    [InlineData("Trivial", "5")]
    [InlineData(" hIgHeSt ", "1")]
    [InlineData("Urgent", null)] // unknown: no priority, not a guess
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_priority_name_maps_to_the_one_to_five_scale_or_to_none(string? name, string? expected) =>
        Assert.Equal(expected, JiraMapping.Priority(name));

    [Theory]
    // category, status name, resolution, in a sprint -> status
    [InlineData("new", "To Do", null, false, IssueStatuses.Backlog)]
    [InlineData("new", "To Do", null, true, IssueStatuses.Ready)]
    [InlineData("new", "Open", "Fixed", false, IssueStatuses.Backlog)] // a resolution on an open issue changes nothing
    [InlineData("indeterminate", "In Progress", null, false, IssueStatuses.InProgress)]
    [InlineData("indeterminate", "In Progress", null, true, IssueStatuses.InProgress)]
    [InlineData("indeterminate", "In Review", null, true, IssueStatuses.InReview)]
    [InlineData("indeterminate", "CODE REVIEW", null, false, IssueStatuses.InReview)]
    [InlineData("indeterminate", "Peer Review (QA)", null, false, IssueStatuses.InReview)]
    [InlineData("done", "Done", null, false, IssueStatuses.Done)]
    [InlineData("done", "Done", "Done", true, IssueStatuses.Done)]
    [InlineData("done", "Closed", "Fixed", false, IssueStatuses.Done)]
    [InlineData("done", "Closed", "Won't Do", false, IssueStatuses.Cancelled)]
    [InlineData("done", "Closed", "Wont Do", false, IssueStatuses.Cancelled)]
    [InlineData("done", "Closed", "Won't Fix", false, IssueStatuses.Cancelled)]
    [InlineData("done", "Closed", "Cannot Reproduce", false, IssueStatuses.Cancelled)]
    [InlineData("done", "Closed", "Duplicate", false, IssueStatuses.Cancelled)]
    [InlineData("done", "Closed", "Declined", false, IssueStatuses.Cancelled)]
    [InlineData("done", "Closed", "  rejected ", false, IssueStatuses.Cancelled)]
    [InlineData("DONE", "Done", null, false, IssueStatuses.Done)]
    [InlineData("undefined", "Whatever", null, false, IssueStatuses.Backlog)] // an unknown category is a waiting issue
    [InlineData("undefined", "Whatever", null, true, IssueStatuses.Ready)]
    public void A_status_comes_from_the_category_the_name_the_resolution_and_the_sprint(string category, string name, string? resolution, bool inSprint, string expected) =>
        Assert.Equal(expected, JiraMapping.Status(category, name, resolution, inSprint));

    [Theory]
    [InlineData("active", SprintStatuses.Active)]
    [InlineData("ACTIVE", SprintStatuses.Active)]
    [InlineData("closed", SprintStatuses.Closed)]
    [InlineData(" Closed ", SprintStatuses.Closed)]
    [InlineData("future", SprintStatuses.Planned)]
    [InlineData("", SprintStatuses.Planned)]
    [InlineData("paused", SprintStatuses.Planned)]
    public void A_sprint_state_maps_to_planned_active_or_closed(string state, string expected) =>
        Assert.Equal(expected, JiraMapping.Sprint(state));

    [Theory]
    [InlineData(5.0, "5")]
    [InlineData(2.5, "2.5")]
    [InlineData(0.0, "0")]
    [InlineData(0.333, "0.33")]
    [InlineData(1.006, "1.01")]
    [InlineData(9999.99, "9999.99")]
    [InlineData(-1.0, null)]
    [InlineData(10000.0, null)]
    [InlineData(double.NaN, null)]
    [InlineData(double.PositiveInfinity, null)]
    [InlineData(null, null)]
    public void Story_points_are_kept_with_at_most_two_decimals_or_dropped(double? points, string? expected) =>
        Assert.Equal(expected, JiraMapping.Estimate(points));

    [Fact]
    public void Text_from_the_tool_is_cleaned_trimmed_and_cut()
    {
        Assert.Equal("a\tb\nc", JiraMapping.Text("  a\tb\nc\u0000\u0007  ", 100));
        Assert.Equal("abcd", JiraMapping.Text("abcdefgh", 4));
        Assert.Null(JiraMapping.Text("   ", 10));
        Assert.Null(JiraMapping.Text("\u0000\u0001", 10)); // only control characters: nothing left
        Assert.Null(JiraMapping.Text(null, 10));
    }
}
