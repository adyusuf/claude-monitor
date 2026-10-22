using System.Globalization;
using System.Text.Json;

namespace Podium.Api.Integrations;

/// <summary>The Jira field ids that differ per site (found once from <c>GET /field</c>).</summary>
public sealed record JiraFields(string? SprintId, string? StoryPointsId);

/// <summary>Jira JSON to the tracker records. Tolerant of missing parts: a field the site does not have is just absent.</summary>
public static class JiraParse
{
    public const string BaseFields = "summary,description,issuetype,priority,status,resolution,assignee,reporter,parent,updated";

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool Has(JsonElement e, string name, out JsonElement value)
    {
        value = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }

    public static ExternalPerson? Person(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || Str(e, "accountId") is not { Length: > 0 } id) return null;
        var type = Str(e, "accountType");
        var active = !(e.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.False)
                     && (type is null or "atlassian");
        return new ExternalPerson(id, JiraMapping.Text(Str(e, "displayName"), 200) ?? id, JiraMapping.Text(Str(e, "emailAddress"), 254), active);
    }

    public static ExternalContainer Container(JsonElement p) => new(
        Str(p, "id") ?? "",
        Str(p, "key") ?? "",
        JiraMapping.Text(Str(p, "name"), 200) ?? Str(p, "key") ?? "",
        JiraMapping.Text(Str(p, "description"), 2000),
        Has(p, "lead", out var lead) ? Person(lead) : null,
        Str(p, "self"));

    public static ExternalIteration? Iteration(JsonElement s, string containerKey)
    {
        var id = s.TryGetProperty("id", out var i) ? i.ToString() : null;
        if (id is not { Length: > 0 }) return null;
        return new ExternalIteration(id, JiraMapping.Text(Str(s, "name"), 200) ?? $"Sprint {id}", Str(s, "state") ?? "future",
            JiraMapping.Text(Str(s, "goal"), 1000), Date(Str(s, "startDate")), Date(Str(s, "endDate")), containerKey);
    }

    /// <summary>The newest sprint of an issue (the highest id), from the agile API's <c>sprint</c> field or the site's own sprint field.</summary>
    public static ExternalIteration? NewestIteration(JsonElement fields, JiraFields known, string containerKey)
    {
        ExternalIteration? best = null;
        long bestId = long.MinValue;
        void Consider(JsonElement s)
        {
            if (Iteration(s, containerKey) is { } it && long.TryParse(it.Id, CultureInfo.InvariantCulture, out var n) && n > bestId)
            {
                best = it;
                bestId = n;
            }
        }

        if (Has(fields, "sprint", out var one) && one.ValueKind == JsonValueKind.Object) Consider(one);
        if (known.SprintId is { } sid && Has(fields, sid, out var many) && many.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in many.EnumerateArray()) Consider(s);
        }

        return best;
    }

    public static ExternalItem? Item(JsonElement issue, JiraFields known, string? containerKeyHint = null)
    {
        if (Str(issue, "key") is not { Length: > 0 } key || !issue.TryGetProperty("fields", out var f) || f.ValueKind != JsonValueKind.Object) return null;
        var container = containerKeyHint ?? key[..Math.Max(key.LastIndexOf('-'), 0)];
        var type = Has(f, "issuetype", out var t) ? t : default;
        var status = Has(f, "status", out var st) ? st : default;
        var category = Has(status, "statusCategory", out var sc) ? Str(sc, "key") ?? "new" : "new";
        var parent = Has(f, "parent", out var par) ? par : default;
        var parentType = Has(parent, "fields", out var pf) && Has(pf, "issuetype", out var pt) ? Str(pt, "name") : null;
        double? points = known.StoryPointsId is { } spid && Has(f, spid, out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetDouble() : null;
        return new ExternalItem(
            Str(issue, "id") ?? key,
            key,
            container,
            JiraMapping.Text(Str(f, "summary"), JiraMapping.TitleMax) ?? key,
            Has(f, "description", out var d) ? JiraAdf.ToText(d, JiraMapping.TextMax) : null,
            Str(type, "name") ?? "Task",
            type.ValueKind == JsonValueKind.Object && type.TryGetProperty("subtask", out var sub) && sub.ValueKind == JsonValueKind.True,
            Str(parent, "key"),
            parentType,
            Has(f, "priority", out var pr) ? Str(pr, "name") : null,
            Str(status, "name") ?? "",
            category,
            Has(f, "resolution", out var res) ? Str(res, "name") : null,
            JiraMapping.Estimate(points),
            Has(f, "assignee", out var asg) ? Person(asg) : null,
            Has(f, "reporter", out var rep) ? Person(rep) : null,
            NewestIteration(f, known, container),
            Str(f, "updated") ?? "");
    }

    public static DateOnly? Date(string? value) =>
        value is { Length: >= 10 } && DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
