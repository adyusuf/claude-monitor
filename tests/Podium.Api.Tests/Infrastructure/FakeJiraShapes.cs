using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Podium.Api.Tests.Infrastructure;

public sealed partial class FakeJiraHandler
{
    // ---- JSON shapes --------------------------------------------------------------------------------------------------------------

    private static JsonArray Array(IEnumerable<JsonNode> nodes) => new([.. nodes]);

    private static JsonNode PersonJson(FakePerson p)
    {
        var json = new JsonObject { ["accountId"] = p.AccountId, ["displayName"] = p.DisplayName, ["accountType"] = p.AccountType, ["active"] = p.Active };
        if (p.Email is not null) json["emailAddress"] = p.Email;
        return json;
    }

    private static JsonNode SpaceJson(FakeSpace s)
    {
        var json = new JsonObject { ["id"] = s.Id, ["key"] = s.Key, ["name"] = s.Name, ["self"] = "https://api.atlassian.com/project/" + s.Id };
        if (s.Description is not null) json["description"] = s.Description;
        if (s.Lead is not null) json["lead"] = PersonJson(s.Lead);
        return json;
    }

    private static JsonNode SprintJson(FakeSprint s)
    {
        var json = new JsonObject { ["id"] = s.Id, ["name"] = s.Name, ["state"] = s.State, ["originBoardId"] = s.BoardId };
        if (s.Goal is not null) json["goal"] = s.Goal;
        if (s.Start is not null) json["startDate"] = s.Start;
        if (s.End is not null) json["endDate"] = s.End;
        return json;
    }

    private static JsonObject IssueJson(FakeSite site, FakeIssue i, bool agile)
    {
        var parent = i.ParentKey is null ? null : site.Spaces.SelectMany(x => x.Issues).FirstOrDefault(p => p.Key == i.ParentKey);
        var fields = new JsonObject
        {
            ["summary"] = i.Summary,
            ["description"] = i.Description is null ? null : Adf(i.Description),
            ["issuetype"] = new JsonObject { ["name"] = i.Type, ["subtask"] = i.IsSubtask },
            ["priority"] = i.Priority is null ? null : new JsonObject { ["name"] = i.Priority },
            ["status"] = new JsonObject { ["name"] = i.Status, ["statusCategory"] = new JsonObject { ["key"] = i.Category } },
            ["resolution"] = i.Resolution is null ? null : new JsonObject { ["name"] = i.Resolution },
            ["assignee"] = i.Assignee is null ? null : PersonJson(i.Assignee),
            ["reporter"] = i.Reporter is null ? null : PersonJson(i.Reporter),
            ["parent"] = i.ParentKey is null ? null : new JsonObject { ["key"] = i.ParentKey, ["fields"] = new JsonObject { ["issuetype"] = new JsonObject { ["name"] = parent?.Type ?? "Story" } } },
            ["updated"] = FakeJira.Stamp(i.Updated),
            ["customfield_10016"] = i.Points,
        };
        var sprints = i.PastSprintIds.Concat(i.SprintId is { } now ? [now] : []).Select(id => site.Sprints[id]).ToList();
        fields["customfield_10020"] = sprints.Count == 0 ? null : Array(sprints.Select(SprintJson));
        if (agile && i.SprintId is { } current) fields["sprint"] = SprintJson(site.Sprints[current]);
        return new JsonObject { ["id"] = i.Id, ["key"] = i.Key, ["fields"] = fields };
    }

    private static JsonObject Adf(string text) => new JsonObject
    {
        ["type"] = "doc",
        ["version"] = 1,
        ["content"] = Array(text.Split('\n').Select(l => (JsonNode)new JsonObject
        {
            ["type"] = "paragraph",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = l }),
        })),
    };

    private static HttpResponseMessage Json(JsonNode node, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode status) =>
        new(status) { Content = new StringContent(status == HttpStatusCode.Accepted ? "" : "{\"message\":\"secret detail of the failure\"}", Encoding.UTF8, "application/json") };
}
