using System.Text.Json;
using ClaudeMonitor.Api.Integrations;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Jira JSON to the tracker records (ADR-0015 §3, §4): the whole issue, the sprint of an issue, and every missing part.</summary>
public sealed class JiraParseTests
{
    private static readonly JiraFields Known = new("customfield_10020", "customfield_10016");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private const string FullIssue = """
        {
          "id": "10042", "key": "ABC-12",
          "fields": {
            "summary": "Fix the cart",
            "description": {"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"It breaks."}]}]},
            "issuetype": {"name": "Bug", "subtask": false},
            "priority": {"name": "High"},
            "status": {"name": "In Review", "statusCategory": {"key": "indeterminate"}},
            "resolution": {"name": "Fixed"},
            "assignee": {"accountId": "u-1", "displayName": "Ada", "emailAddress": "ada@example.invalid", "accountType": "atlassian", "active": true},
            "reporter": {"accountId": "u-2", "displayName": "Bob"},
            "parent": {"key": "ABC-1", "fields": {"issuetype": {"name": "Epic"}}},
            "updated": "2026-11-02T10:00:00.000+0000",
            "customfield_10016": 3.5,
            "customfield_10020": [{"id": 5, "name": "Sprint 5", "state": "closed"}, {"id": 9, "name": "Sprint 9", "state": "active", "goal": "Ship"}]
          }
        }
        """;

    [Fact]
    public void An_issue_is_read_in_full()
    {
        var item = JiraParse.Item(Json(FullIssue), Known)!;

        Assert.Equal(("10042", "ABC-12", "ABC", "Fix the cart", "It breaks.", "Bug", false), (item.Id, item.Key, item.ContainerKey, item.Summary, item.Description, item.TypeName, item.IsSubtask));
        Assert.Equal(("ABC-1", "Epic", "High", "In Review", "indeterminate", "Fixed", "3.5"), (item.ParentKey, item.ParentTypeName, item.PriorityName, item.StatusName, item.StatusCategory, item.Resolution, item.Estimate));
        Assert.Equal(("u-1", "Ada", "ada@example.invalid", true), (item.Assignee!.Id, item.Assignee.DisplayName, item.Assignee.Email, item.Assignee.Active));
        Assert.Equal("u-2", item.Reporter!.Id);
        Assert.Null(item.Reporter.Email);
        Assert.Equal("2026-11-02T10:00:00.000+0000", item.UpdatedStamp);
        Assert.Equal(("9", "Sprint 9", "active", "Ship", "ABC"), (item.Iteration!.Id, item.Iteration.Name, item.Iteration.State, item.Iteration.Goal, item.Iteration.ContainerKey));
    }

    [Fact]
    public void A_container_hint_overrides_the_key_prefix_and_a_sub_task_is_flagged()
    {
        var issue = Json("""{"id":"1","key":"ABC-2","fields":{"issuetype":{"name":"Sub-task","subtask":true},"parent":{"key":"ABC-1","fields":{"issuetype":{"name":"Story"}}}}}""");

        var item = JiraParse.Item(issue, Known, "OTHER")!;

        Assert.Equal(("OTHER", true, "ABC-1", "Story"), (item.ContainerKey, item.IsSubtask, item.ParentKey, item.ParentTypeName));
    }

    [Fact]
    public void An_issue_with_nothing_but_a_key_gets_safe_defaults()
    {
        var item = JiraParse.Item(Json("""{"key":"ABC-3","fields":{}}"""), Known)!;

        Assert.Equal(("ABC-3", "ABC-3", "Task", "", "new", false), (item.Id, item.Summary, item.TypeName, item.StatusName, item.StatusCategory, item.IsSubtask));
        Assert.Null(item.Description);
        Assert.Null(item.Assignee);
        Assert.Null(item.Iteration);
        Assert.Null(item.Estimate);
        Assert.Null(item.PriorityName);
        Assert.Equal("", item.UpdatedStamp);
    }

    [Theory]
    [InlineData("""{"fields":{}}""")]
    [InlineData("""{"key":"","fields":{}}""")]
    [InlineData("""{"key":"ABC-1"}""")]
    [InlineData("""{"key":"ABC-1","fields":"nope"}""")]
    [InlineData("""[]""")]
    public void Something_that_is_not_an_issue_is_nothing(string json) => Assert.Null(JiraParse.Item(Json(json), Known));

    [Fact]
    public void Story_points_need_the_sites_field_and_a_number()
    {
        string Issue(string points) => """{"key":"A-1","fields":{"customfield_10016":""" + points + "}}";

        Assert.Equal("8", JiraParse.Item(Json(Issue("8")), Known)!.Estimate);
        Assert.Null(JiraParse.Item(Json(Issue("\"8\"")), Known)!.Estimate);
        Assert.Null(JiraParse.Item(Json(Issue("null")), Known)!.Estimate);
        Assert.Null(JiraParse.Item(Json(Issue("8")), new JiraFields(null, null))!.Estimate); // the site has no such field
    }

    [Theory]
    [InlineData("""{"sprint":{"id":7,"name":"Agile","state":"active"}}""", "7")]
    [InlineData("""{"customfield_10020":[{"id":5,"name":"a","state":"closed"},{"id":9,"name":"b","state":"active"},{"id":6,"name":"c","state":"closed"}]}""", "9")]
    [InlineData("""{"sprint":{"id":7,"name":"Agile","state":"active"},"customfield_10020":[{"id":5,"name":"a","state":"closed"}]}""", "7")]
    [InlineData("""{"sprint":{"id":7,"name":"Agile","state":"active"},"customfield_10020":[{"id":5,"name":"a","state":"closed"},{"id":12,"name":"z","state":"future"}]}""", "12")]
    [InlineData("""{"sprint":{"id":"8","name":"as text","state":"active"}}""", "8")]
    public void The_newest_sprint_wins_whichever_field_it_comes_from(string fields, string expectedId) =>
        Assert.Equal(expectedId, JiraParse.NewestIteration(Json(fields), Known, "ABC")!.Id);

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"sprint":null,"customfield_10020":null}""")]
    [InlineData("""{"customfield_10020":[]}""")]
    [InlineData("""{"customfield_10020":"not an array"}""")]
    [InlineData("""{"sprint":{"name":"no id"}}""")]
    [InlineData("""{"sprint":{"id":"abc","name":"not a number"}}""")]
    public void An_issue_with_no_usable_sprint_has_none(string fields) => Assert.Null(JiraParse.NewestIteration(Json(fields), Known, "ABC"));

    [Fact]
    public void A_site_without_a_sprint_field_still_reads_the_agile_sprint()
    {
        var fields = Json("""{"sprint":{"id":3,"name":"S","state":"future"},"customfield_10020":[{"id":99,"name":"hidden","state":"active"}]}""");

        Assert.Equal("3", JiraParse.NewestIteration(fields, new JiraFields(null, null), "ABC")!.Id);
    }

    [Fact]
    public void A_sprint_has_defaults_dates_and_caps()
    {
        var sprint = JiraParse.Iteration(Json("""{"id":14,"state":"closed","startDate":"2026-11-02T09:00:00.000Z","endDate":"2026-11-13T17:00:00.000Z","goal":"G"}"""), "ABC")!;
        var bare = JiraParse.Iteration(Json("""{"id":15,"startDate":"soon","endDate":"2026-13-45"}"""), "ABC")!;

        Assert.Equal(("14", "Sprint 14", "closed", "G", new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 13)), (sprint.Id, sprint.Name, sprint.State, sprint.Goal, sprint.Start, sprint.End));
        Assert.Equal(("future", null, null), (bare.State, bare.Start, bare.End));
        Assert.Null(JiraParse.Iteration(Json("""{"name":"no id"}"""), "ABC"));
        Assert.Equal(1000, JiraParse.Iteration(Json($$"""{"id":1,"goal":"{{new string('g', 1500)}}"}"""), "ABC")!.Goal!.Length);
    }

    [Fact]
    public void A_person_needs_an_account_id_and_an_app_or_inactive_account_is_not_active()
    {
        Assert.Null(JiraParse.Person(Json("""{"displayName":"No id"}""")));
        Assert.Null(JiraParse.Person(Json("""{"accountId":""}""")));
        Assert.Null(JiraParse.Person(Json("[]")));
        Assert.Equal("u-1", JiraParse.Person(Json("""{"accountId":"u-1"}"""))!.DisplayName); // no name: the id
        Assert.True(JiraParse.Person(Json("""{"accountId":"u-1"}"""))!.Active);
        Assert.False(JiraParse.Person(Json("""{"accountId":"u-1","accountType":"app"}"""))!.Active);
        Assert.False(JiraParse.Person(Json("""{"accountId":"u-1","accountType":"customer"}"""))!.Active);
        Assert.False(JiraParse.Person(Json("""{"accountId":"u-1","active":false}"""))!.Active);
        Assert.Equal("a@example.invalid", JiraParse.Person(Json("""{"accountId":"u-1","emailAddress":"  a@example.invalid "}"""))!.Email);
    }

    [Fact]
    public void A_container_reads_its_lead_and_caps_the_description()
    {
        var space = JiraParse.Container(Json($$"""{"id":"10","key":"ABC","name":"Alpha","description":"{{new string('d', 3000)}}","lead":{"accountId":"u-9","displayName":"Lead"},"self":"https://x.invalid/p/10"}"""));
        var bare = JiraParse.Container(Json("""{"key":"XYZ"}"""));

        Assert.Equal(("10", "ABC", "Alpha", 2000, "u-9", "https://x.invalid/p/10"), (space.Id, space.Key, space.Name, space.Description!.Length, space.Lead!.Id, space.Url));
        Assert.Equal(("", "XYZ", "XYZ"), (bare.Id, bare.Key, bare.Name)); // no name: the key
        Assert.Null(bare.Lead);
    }

    [Fact]
    public void A_long_summary_is_cut_to_the_title_limit()
    {
        var item = JiraParse.Item(Json("{\"key\":\"A-1\",\"fields\":{\"summary\":\"" + new string('s', 500) + "\"}}"), Known)!;

        Assert.Equal(JiraMapping.TitleMax, item.Summary.Length);
    }
}
