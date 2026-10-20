using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeMonitor.Api.Tests.Infrastructure;

/// <summary>A status of the fake site's workflow. Any status can be reached from any other (a test removes one to make a step unavailable).</summary>
public sealed record FakeStatus(string Id, string Name, string Category);

public sealed partial class FakeSite
{
    private readonly List<(HttpMethod Method, string Contains, HttpStatusCode Status, int Left)> refusals = [];

    /// <summary>The workflow: the transitions an issue offers are these statuses but its own, in this order, with the id <c>t</c> + the status id.</summary>
    public List<FakeStatus> Workflow { get; } =
    [
        new("11", "To Do", "new"),
        new("21", "In Progress", "indeterminate"),
        new("22", "In Review", "indeterminate"),
        new("31", "Done", "done"),
        new("32", "Won't Do", "done"),
    ];

    /// <summary>The next calls of this method to a path containing the text answer with this status (a refusal of one write, whatever else was read before it).</summary>
    public void Refuse(HttpMethod method, string contains, HttpStatusCode status, int times = 1)
    {
        lock (refusals) refusals.Add((method, contains, status, times));
    }

    public HttpStatusCode? TakeRefusal(HttpMethod method, string path)
    {
        lock (refusals)
        {
            var at = refusals.FindIndex(r => r.Left > 0 && r.Method == method && path.Contains(r.Contains, StringComparison.Ordinal));
            if (at < 0) return null;
            var r = refusals[at];
            refusals[at] = r with { Left = r.Left - 1 };
            return r.Status;
        }
    }

    public FakeIssue? Find(string key) => Spaces.Where(s => !s.Deleted).SelectMany(s => s.Issues).FirstOrDefault(i => !i.Deleted && i.Key == key);

    public FakePerson? PersonById(string accountId) => Spaces.SelectMany(s => s.People).FirstOrDefault(p => p.AccountId == accountId);
}

/// <summary>The write endpoints of the fake: they change the model, answer in Jira's shapes and refuse what Jira refuses (an unknown field, a bad id).</summary>
public sealed partial class FakeJiraHandler
{
    private const int MaxKeys = 50;

    private HttpResponseMessage? Written(FakeSite site, HttpMethod method, string rest, string body)
    {
        var issuePath = IssueWritePath().Match(rest);
        var agile = rest.StartsWith("/rest/agile/1.0/", StringComparison.Ordinal) && method != HttpMethod.Get;
        var isWrite = (method == HttpMethod.Put && issuePath.Success) || (method == HttpMethod.Post && rest == "/rest/api/3/issue") || agile
                      || (issuePath.Success && issuePath.Groups["tail"].Value == "/transitions");
        if (!isWrite) return null;
        if (site.TakeRefusal(method, rest) is { } refused) return Status(refused);
        if (issuePath.Success && issuePath.Groups["tail"].Value == "/transitions") return Transitions(site, method, Uri.UnescapeDataString(issuePath.Groups["key"].Value), body);
        if (issuePath.Success) return Edit(site, Uri.UnescapeDataString(issuePath.Groups["key"].Value), body);
        return agile ? AgileWrite(site, method, rest["/rest/agile/1.0".Length..], body) : Create(site, body);
    }

    private HttpResponseMessage Transitions(FakeSite site, HttpMethod method, string key, string body)
    {
        if (site.Find(key) is not { } issue) return Status(HttpStatusCode.NotFound);
        if (method == HttpMethod.Get)
        {
            var offered = site.Workflow.Where(s => s.Name != issue.Status).Select(s => (JsonNode)new JsonObject
            {
                ["id"] = "t" + s.Id,
                ["name"] = "Move to " + s.Name,
                ["to"] = new JsonObject { ["name"] = s.Name, ["statusCategory"] = new JsonObject { ["key"] = s.Category } },
            });
            return Json(new JsonObject { ["transitions"] = Array(offered) });
        }

        var id = JsonNode.Parse(body)?["transition"]?["id"]?.GetValue<string>();
        if (site.Workflow.FirstOrDefault(s => "t" + s.Id == id && s.Name != issue.Status) is not { } to) return Bad("transition not available");
        issue.Status = to.Name;
        issue.Category = to.Category;
        issue.Resolution = to.Category == "done" ? (to.Name == "Won't Do" ? "Won't Do" : "Done") : null;
        jira.Touch(issue);
        return Status(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage Edit(FakeSite site, string key, string body)
    {
        if (site.Find(key) is not { } issue) return Status(HttpStatusCode.NotFound);
        var error = Apply(site, issue, JsonNode.Parse(body)?["fields"]?.AsObject() ?? []);
        if (error is not null) return Bad(error);
        jira.Touch(issue);
        return Status(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage Create(FakeSite site, string body)
    {
        var fields = JsonNode.Parse(body)?["fields"]?.AsObject() ?? [];
        var space = site.Spaces.FirstOrDefault(s => !s.Deleted && s.Key == fields["project"]?["key"]?.GetValue<string>());
        if (space is null || fields["issuetype"]?["name"]?.GetValue<string>() is not { } type) return Bad("project or issue type missing");
        var issue = new FakeIssue(jira.NextId().ToString(CultureInfo.InvariantCulture), $"{space.Key}-{space.Issues.Count + 1}", "") { Type = type, Priority = null };
        var error = Apply(site, issue, fields);
        if (error is not null || string.IsNullOrWhiteSpace(issue.Summary)) return Bad(error ?? "summary is required");
        jira.Touch(issue);
        space.Issues.Add(issue);
        return Json(new JsonObject { ["id"] = issue.Id, ["key"] = issue.Key }, HttpStatusCode.Created);
    }

    /// <summary>Applies the fields a client may set; any other name is refused, as Jira refuses a field that is not on the screen.</summary>
    private static string? Apply(FakeSite site, FakeIssue issue, JsonObject fields)
    {
        foreach (var (name, value) in fields)
        {
            switch (name)
            {
                case "project" or "issuetype":
                    break;
                case "summary":
                    issue.Summary = value?.GetValue<string>() ?? "";
                    break;
                case "description":
                    issue.Description = value is JsonObject adf ? Plain(adf) : null;
                    break;
                case "priority":
                    issue.Priority = value?["name"]?.GetValue<string>();
                    break;
                case "assignee":
                    var id = value?["accountId"]?.GetValue<string>();
                    issue.Assignee = id is null ? null : site.PersonById(id) ?? new FakePerson(id, "Account " + id);
                    break;
                case "parent":
                    issue.ParentKey = value?["key"]?.GetValue<string>();
                    break;
                case "customfield_10016":
                    issue.Points = value?.GetValue<double>();
                    break;
                default:
                    return $"Field '{name}' cannot be set.";
            }
        }

        return null;
    }

    /// <summary>The text of an Atlassian document: one line per paragraph (the inverse of what <see cref="Adf"/> builds).</summary>
    private static string Plain(JsonObject doc) => string.Join('\n', (doc["content"]?.AsArray() ?? []).Select(p => string.Concat((p?["content"]?.AsArray() ?? []).Select(t => t?["text"]?.GetValue<string>()))));

    private static HttpResponseMessage AgileWrite(FakeSite site, HttpMethod method, string rest, string body)
    {
        var json = body.Length == 0 ? new JsonObject() : JsonNode.Parse(body)!.AsObject();
        if (method == HttpMethod.Put && rest == "/issue/rank") return Rank(site, json);
        if (method == HttpMethod.Post && rest == "/backlog/issue") return Place(site, null, json);
        if (method == HttpMethod.Post && rest == "/sprint") return NewSprint(site, json);
        var sprint = SprintWritePath().Match(rest);
        if (!sprint.Success || !site.Sprints.TryGetValue(long.Parse(sprint.Groups["id"].Value, CultureInfo.InvariantCulture), out var found) || found.Deleted) return Status(HttpStatusCode.NotFound);
        if (sprint.Groups["issue"].Success) return method == HttpMethod.Post ? Place(site, found, json) : Status(HttpStatusCode.NotFound);
        if (method == HttpMethod.Delete) return DropSprint(site, found);
        return method == HttpMethod.Post ? ChangeSprint(found, json) : Status(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Rank(FakeSite site, JsonObject json)
    {
        var moved = (json["issues"]?.AsArray() ?? []).Select(k => site.Find(k!.GetValue<string>())).ToList();
        var anchor = site.Find((json["rankAfterIssue"] ?? json["rankBeforeIssue"])?.GetValue<string>() ?? "");
        var space = site.Spaces.FirstOrDefault(s => s.Issues.Contains(anchor!));
        if (moved.Count == 0 || moved.Contains(null) || anchor is null || space is null || moved.Any(m => !space.Issues.Contains(m!))) return Bad("rank anchor or issue not found");
        space.Issues.RemoveAll(i => moved.Contains(i));
        var at = space.Issues.IndexOf(anchor) + (json["rankAfterIssue"] is not null ? 1 : 0);
        space.Issues.InsertRange(at, moved!);
        return Status(HttpStatusCode.NoContent);
    }

    private static HttpResponseMessage Place(FakeSite site, FakeSprint? sprint, JsonObject json)
    {
        var keys = (json["issues"]?.AsArray() ?? []).Select(k => k!.GetValue<string>()).ToList();
        if (keys.Count is 0 or > MaxKeys) return Bad("between one and 50 issues");
        if (sprint is { State: "closed" }) return Bad("the sprint is closed");
        var issues = keys.Select(site.Find).ToList();
        if (issues.Contains(null)) return Bad("an issue does not exist");
        foreach (var issue in issues)
        {
            issue!.SprintId = sprint?.Id;
            site.Jira.Touch(issue); // Jira's "updated" moves with the sprint field
        }

        return Status(HttpStatusCode.NoContent);
    }

    private static HttpResponseMessage NewSprint(FakeSite site, JsonObject json)
    {
        var space = site.Spaces.FirstOrDefault(s => !s.Deleted && s.BoardId is not null && s.BoardId == json["originBoardId"]?.GetValue<long>());
        if (space is null || string.IsNullOrWhiteSpace(json["name"]?.GetValue<string>())) return Bad("board or name missing");
        var sprint = site.Sprint(space, json["name"]!.GetValue<string>(), "future", json["startDate"]?.GetValue<string>(), json["endDate"]?.GetValue<string>());
        sprint.Goal = json["goal"]?.GetValue<string>();
        return Json(SprintJson(sprint), HttpStatusCode.Created);
    }

    private static HttpResponseMessage ChangeSprint(FakeSprint sprint, JsonObject json)
    {
        if (sprint.State == "closed") return Bad("a closed sprint cannot change");
        var state = json["state"]?.GetValue<string>();
        var start = json["startDate"]?.GetValue<string>() ?? sprint.Start;
        var end = json["endDate"]?.GetValue<string>() ?? sprint.End;
        var legal = state is null || (state == "active" && sprint.State == "future" && start is not null && end is not null) || (state == "closed" && sprint.State == "active");
        if (!legal) return Bad("the sprint cannot go to that state");
        if (json["name"] is { } name) sprint.Name = name.GetValue<string>();
        if (json["goal"] is { } goal) sprint.Goal = goal.GetValue<string>();
        sprint.Start = start;
        sprint.End = end;
        if (state is not null) sprint.State = state;
        return Json(SprintJson(sprint));
    }

    private static HttpResponseMessage DropSprint(FakeSite site, FakeSprint sprint)
    {
        if (sprint.State != "future") return Bad("only a planned sprint can be deleted");
        sprint.Deleted = true;
        foreach (var issue in site.Spaces.SelectMany(s => s.Issues).Where(i => i.SprintId == sprint.Id)) issue.SprintId = null;
        return Status(HttpStatusCode.NoContent);
    }

    private static HttpResponseMessage Bad(string message) =>
        new(HttpStatusCode.BadRequest) { Content = new StringContent(new JsonObject { ["errorMessages"] = new JsonArray(message) }.ToJsonString(), Encoding.UTF8, "application/json") };

    [GeneratedRegex("^/rest/api/3/issue/(?<key>[^/]+)(?<tail>/transitions)?$")]
    private static partial Regex IssueWritePath();

    [GeneratedRegex("^/sprint/(?<id>\\d+)(?<issue>/issue)?$")]
    private static partial Regex SprintWritePath();
}
