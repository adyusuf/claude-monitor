using System.Globalization;
using System.Text.Json;

namespace ClaudeMonitor.Api.Integrations;

/// <summary>The Jira Cloud adapter (ADR-0015 §3). Reads only; the write path is <see cref="JiraWriter"/>.</summary>
public sealed class JiraTracker(JiraApi api) : IWorkTracker
{
    private const int PageSize = 50;
    private const int PageCap = 200; // 10 000 rows: a ceiling against a runaway loop, not a limit anybody should reach
    private readonly Dictionary<string, JiraFields> fieldCache = [];

    public string Provider => Providers.Jira;

    public async Task<IReadOnlyList<ExternalContainer>> ListContainersAsync(TrackerContext ctx, CancellationToken ct)
    {
        var result = new List<ExternalContainer>();
        for (var start = 0; start < PageSize * PageCap; start += PageSize)
        {
            using var page = await api.GetAsync(ctx, $"/rest/api/3/project/search?startAt={start}&maxResults={PageSize}&expand=description,lead", ct);
            var values = page.RootElement.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : [];
            result.AddRange(values.Select(JiraParse.Container).Where(c => c.Key.Length > 0));
            if (values.Count < PageSize || (page.RootElement.TryGetProperty("isLast", out var last) && last.ValueKind == JsonValueKind.True)) break;
        }

        return result;
    }

    public async Task<ExternalContainer?> GetContainerAsync(TrackerContext ctx, string key, CancellationToken ct)
    {
        try
        {
            using var doc = await api.GetAsync(ctx, $"/rest/api/3/project/{Uri.EscapeDataString(key)}?expand=description,lead", ct);
            return JiraParse.Container(doc.RootElement);
        }
        catch (TrackerException e) when (e.Code == TrackerException.Codes.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ExternalIteration>> ListIterationsAsync(TrackerContext ctx, string containerKey, CancellationToken ct)
    {
        var sprints = new Dictionary<string, ExternalIteration>();
        foreach (var board in await BoardsAsync(ctx, containerKey, ct))
        {
            await foreach (var s in PagedAsync(ctx, $"/rest/agile/1.0/board/{board}/sprint?maxResults={PageSize}", "values", ct, tolerate: true))
            {
                if (JiraParse.Iteration(s, containerKey) is { } it) sprints.TryAdd(it.Id, it);
            }
        }

        return [.. sprints.Values.OrderBy(s => long.TryParse(s.Id, CultureInfo.InvariantCulture, out var n) ? n : long.MaxValue)];
    }

    public async Task<IReadOnlyList<ExternalItem>> ListBacklogAsync(TrackerContext ctx, string containerKey, CancellationToken ct)
    {
        var fields = await FieldsAsync(ctx, ct);
        var boards = await BoardsAsync(ctx, containerKey, ct);
        var items = new List<ExternalItem>();
        // The board's own backlog is the exact one; a space with no board (or a board without a backlog, such as a kanban one) falls back to the JQL.
        foreach (var board in boards)
        {
            var any = false;
            try
            {
                await foreach (var issue in PagedAsync(ctx, $"/rest/agile/1.0/board/{board}/backlog?maxResults={PageSize}&fields={FieldList(fields)}", "issues", ct))
                {
                    any = true;
                    if (JiraParse.Item(issue, fields, containerKey) is { } item) items.Add(item);
                }
            }
            catch (TrackerException e) when (e.Code is TrackerException.Codes.Rejected or TrackerException.Codes.NotFound)
            {
                continue;
            }

            if (any || items.Count > 0) return items;
        }

        return await SearchAsync(ctx, $"project = \"{Jql(containerKey)}\" AND sprint is EMPTY AND statusCategory != Done ORDER BY Rank ASC", fields, containerKey, ct);
    }

    public async Task<IReadOnlyList<ExternalItem>> ListIterationItemsAsync(TrackerContext ctx, string iterationId, CancellationToken ct)
    {
        var fields = await FieldsAsync(ctx, ct);
        var items = new List<ExternalItem>();
        await foreach (var issue in PagedAsync(ctx,
            $"/rest/agile/1.0/sprint/{Uri.EscapeDataString(iterationId)}/issue?maxResults={PageSize}&fields={FieldList(fields)}", "issues", ct))
        {
            if (JiraParse.Item(issue, fields) is { } item) items.Add(item);
        }

        return items;
    }

    public async Task<ExternalItem?> GetItemAsync(TrackerContext ctx, string key, CancellationToken ct)
    {
        var fields = await FieldsAsync(ctx, ct);
        try
        {
            using var doc = await api.GetAsync(ctx, $"/rest/api/3/issue/{Uri.EscapeDataString(key)}?fields={FieldList(fields)}", ct);
            return JiraParse.Item(doc.RootElement, fields);
        }
        catch (TrackerException e) when (e.Code == TrackerException.Codes.NotFound)
        {
            return null;
        }
    }

    public async Task<ExternalIteration?> GetIterationAsync(TrackerContext ctx, string iterationId, CancellationToken ct)
    {
        try
        {
            using var doc = await api.GetAsync(ctx, $"/rest/agile/1.0/sprint/{Uri.EscapeDataString(iterationId)}", ct);
            return JiraParse.Iteration(doc.RootElement, "");
        }
        catch (TrackerException e) when (e.Code == TrackerException.Codes.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ExternalItem>> ListChangedAsync(TrackerContext ctx, IReadOnlyList<string> containerKeys, DateTimeOffset since,
        CancellationToken ct)
    {
        if (containerKeys.Count == 0) return [];
        var fields = await FieldsAsync(ctx, ct);
        var keys = string.Join(", ", containerKeys.Select(k => $"\"{Jql(k)}\""));
        // Jira reads a JQL date in the user's own time zone and to the minute; the overlap of a pass covers that.
        var from = since.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return await SearchAsync(ctx, $"project in ({keys}) AND updated >= \"{from}\" ORDER BY updated ASC", fields, null, ct);
    }

    public async Task<IReadOnlyList<ExternalPerson>> ListPeopleAsync(TrackerContext ctx, string containerKey, CancellationToken ct)
    {
        var people = new Dictionary<string, ExternalPerson>();
        for (var start = 0; start < PageSize * PageCap; start += PageSize)
        {
            using var page = await api.GetAsync(ctx,
                $"/rest/api/3/user/assignable/multiProjectSearch?projectKeys={Uri.EscapeDataString(containerKey)}&startAt={start}&maxResults={PageSize}", ct);
            var rows = page.RootElement.ValueKind == JsonValueKind.Array ? page.RootElement.EnumerateArray().ToList() : [];
            foreach (var row in rows)
            {
                if (JiraParse.Person(row) is { } p) people[p.Id] = p;
            }

            if (rows.Count < PageSize) break;
        }

        return [.. people.Values];
    }

    /// <summary>The site's sprint and story-point field ids, found by their schema and name once per site and tracker.</summary>
    public async Task<JiraFields> FieldsAsync(TrackerContext ctx, CancellationToken ct)
    {
        if (fieldCache.TryGetValue(ctx.SiteId, out var known)) return known;
        string? sprint = null, points = null;
        using var doc = await api.GetAsync(ctx, "/rest/api/3/field", ct);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in doc.RootElement.EnumerateArray())
            {
                var id = JiraParse.Str(f, "id");
                var name = JiraParse.Str(f, "name");
                var custom = f.TryGetProperty("schema", out var schema) ? JiraParse.Str(schema, "custom") : null;
                if (id is null) continue;
                if (custom == "com.pyxis.greenhopper.jira:gh-sprint") sprint ??= id;
                if (points is null && name is "Story point estimate" or "Story Points") points = id;
            }
        }

        return fieldCache[ctx.SiteId] = new JiraFields(sprint, points);
    }

    private async Task<List<string>> BoardsAsync(TrackerContext ctx, string containerKey, CancellationToken ct)
    {
        var boards = new List<string>();
        await foreach (var b in PagedAsync(ctx, $"/rest/agile/1.0/board?projectKeyOrId={Uri.EscapeDataString(containerKey)}&maxResults={PageSize}", "values", ct, tolerate: true))
        {
            if (b.TryGetProperty("id", out var id)) boards.Add(id.ToString());
        }

        return boards;
    }

    private async Task<List<ExternalItem>> SearchAsync(TrackerContext ctx, string jql, JiraFields fields, string? containerKey, CancellationToken ct)
    {
        var items = new List<ExternalItem>();
        string? token = null;
        for (var page = 0; page < PageCap; page++)
        {
            var body = new Dictionary<string, object?>
            {
                ["jql"] = jql,
                ["maxResults"] = 100,
                ["fields"] = FieldList(fields).Split(','),
            };
            if (token is not null) body["nextPageToken"] = token;
            using var doc = await api.PostAsync(ctx, "/rest/api/3/search/jql", body, ct);
            if (doc.RootElement.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
            {
                foreach (var issue in issues.EnumerateArray())
                {
                    if (JiraParse.Item(issue, fields, containerKey) is { } item) items.Add(item);
                }
            }

            token = JiraParse.Str(doc.RootElement, "nextPageToken");
            if (token is null) break;
        }

        return items;
    }

    /// <summary>Pages of the agile API (<c>startAt</c>, a <c>values</c> or <c>issues</c> array, <c>isLast</c> or a short page).</summary>
    private async IAsyncEnumerable<JsonElement> PagedAsync(TrackerContext ctx, string path, string array,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, bool tolerate = false)
    {
        for (var start = 0; start < PageSize * PageCap; start += PageSize)
        {
            JsonDocument doc;
            try
            {
                doc = await api.GetAsync(ctx, $"{path}&startAt={start}", ct);
            }
            catch (TrackerException e) when (tolerate && e.Code is TrackerException.Codes.Rejected or TrackerException.Codes.NotFound or TrackerException.Codes.Forbidden)
            {
                yield break; // a space with no Jira Software, or no board the caller may see, simply has none
            }

            using (doc)
            {
                var rows = doc.RootElement.TryGetProperty(array, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(e => e.Clone()).ToList() : [];
                foreach (var row in rows) yield return row;
                var last = doc.RootElement.TryGetProperty("isLast", out var l) && l.ValueKind == JsonValueKind.True;
                if (rows.Count == 0 || last || (!doc.RootElement.TryGetProperty("isLast", out _) && rows.Count < PageSize)) yield break;
            }
        }
    }

    private static string FieldList(JiraFields known)
    {
        var list = JiraParse.BaseFields;
        if (known.SprintId is { } s) list += "," + s;
        if (known.StoryPointsId is { } p) list += "," + p;
        return list;
    }

    private static string Jql(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
