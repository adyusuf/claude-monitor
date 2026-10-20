using System.Globalization;
using System.Text.Json;
using ClaudeMonitor.Api.Data;

namespace ClaudeMonitor.Api.Integrations;

/// <summary>The Jira Cloud write adapter (ADR-0015 §7). Takes a person's own context; Jira's own permissions have the last word.</summary>
public sealed class JiraWriter(JiraApi api, JiraTracker tracker) : IWorkWriter
{
    private const int KeysMax = 50;

    public async Task<string?> ItemStampAsync(TrackerContext ctx, string key, CancellationToken ct)
    {
        using var doc = await api.GetAsync(ctx, $"/rest/api/3/issue/{Uri.EscapeDataString(key)}?fields=updated", ct);
        return doc.RootElement.TryGetProperty("fields", out var f) ? JiraParse.Str(f, "updated") : null;
    }

    public async Task UpdateItemAsync(TrackerContext ctx, string key, ItemChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.IsEmpty) return;
        var fields = await FieldsAsync(ctx, change, ct);
        using var _ = await api.PutAsync(ctx, $"/rest/api/3/issue/{Uri.EscapeDataString(key)}", new { fields }, ct);
    }

    public async Task<bool> TransitionItemAsync(TrackerContext ctx, string key, string podiumStatus, CancellationToken ct)
    {
        using var list = await api.GetAsync(ctx, $"/rest/api/3/issue/{Uri.EscapeDataString(key)}/transitions", ct);
        var id = list.RootElement.TryGetProperty("transitions", out var t) ? JiraTransitions.Pick(t, podiumStatus) : null;
        if (id is null) return false;
        using var _ = await api.PostAsync(ctx, $"/rest/api/3/issue/{Uri.EscapeDataString(key)}/transitions", new { transition = new { id } }, ct);
        return true;
    }

    public async Task<(string Id, string Key)> CreateItemAsync(TrackerContext ctx, NewItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        var fields = await FieldsAsync(ctx, item.Fields, ct);
        fields["project"] = new { key = item.ContainerKey };
        fields["issuetype"] = new { name = TypeName(item.TypeName) };
        using var doc = await api.PostAsync(ctx, "/rest/api/3/issue", new { fields }, ct);
        var id = JiraParse.Str(doc.RootElement, "id");
        var key = JiraParse.Str(doc.RootElement, "key");
        return id is null || key is null ? throw new TrackerException(TrackerException.Codes.Rejected) : (id, key);
    }

    public async Task RankItemAsync(TrackerContext ctx, string key, string? afterKey, string? beforeKey, CancellationToken ct)
    {
        object body = afterKey is not null ? new { issues = new[] { key }, rankAfterIssue = afterKey } : new { issues = new[] { key }, rankBeforeIssue = beforeKey };
        using var _ = await api.PutAsync(ctx, "/rest/agile/1.0/issue/rank", body, ct);
    }

    public async Task AddToIterationAsync(TrackerContext ctx, string iterationId, IReadOnlyList<string> keys, CancellationToken ct)
    {
        foreach (var chunk in keys.Chunk(KeysMax))
        {
            using var _ = await api.PostAsync(ctx, $"/rest/agile/1.0/sprint/{Uri.EscapeDataString(iterationId)}/issue", new { issues = chunk }, ct);
        }
    }

    public async Task MoveToBacklogAsync(TrackerContext ctx, IReadOnlyList<string> keys, CancellationToken ct)
    {
        foreach (var chunk in keys.Chunk(KeysMax))
        {
            using var _ = await api.PostAsync(ctx, "/rest/agile/1.0/backlog/issue", new { issues = chunk }, ct);
        }
    }

    public async Task<ExternalIteration?> CreateIterationAsync(TrackerContext ctx, string containerKey, IterationChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (await tracker.FirstBoardIdAsync(ctx, containerKey, ct) is not { } board) return null;
        var body = Body(change);
        body["originBoardId"] = long.Parse(board, CultureInfo.InvariantCulture);
        using var doc = await api.PostAsync(ctx, "/rest/agile/1.0/sprint", body, ct);
        return JiraParse.Iteration(doc.RootElement, containerKey);
    }

    public async Task UpdateIterationAsync(TrackerContext ctx, string iterationId, IterationChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        using var _ = await api.PostAsync(ctx, $"/rest/agile/1.0/sprint/{Uri.EscapeDataString(iterationId)}", Body(change), ct);
    }

    public async Task StartIterationAsync(TrackerContext ctx, string iterationId, DateOnly first, DateOnly last, CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["state"] = "active", ["startDate"] = Stamp(first), ["endDate"] = Stamp(last) };
        using var _ = await api.PostAsync(ctx, $"/rest/agile/1.0/sprint/{Uri.EscapeDataString(iterationId)}", body, ct);
    }

    public async Task CloseIterationAsync(TrackerContext ctx, string iterationId, CancellationToken ct)
    {
        using var _ = await api.PostAsync(ctx, $"/rest/agile/1.0/sprint/{Uri.EscapeDataString(iterationId)}", new { state = "closed" }, ct);
    }

    public async Task DeleteIterationAsync(TrackerContext ctx, string iterationId, CancellationToken ct)
    {
        using var _ = await api.SendAsync(ctx, HttpMethod.Delete, $"/rest/agile/1.0/sprint/{Uri.EscapeDataString(iterationId)}", null, ct);
    }

    private async Task<Dictionary<string, object?>> FieldsAsync(TrackerContext ctx, ItemChange c, CancellationToken ct)
    {
        var fields = new Dictionary<string, object?>();
        if (c.Summary is not null) fields["summary"] = c.Summary;
        if (c.SetDescription) fields["description"] = string.IsNullOrWhiteSpace(c.Description) ? null : JiraAdf.FromText(c.Description);
        if (c.SetPriority) fields["priority"] = PriorityName(c.PriorityRank) is { } name ? new { name } : null;
        if (c.SetAssignee) fields["assignee"] = c.AssigneeAccountId is { } id ? new { accountId = id } : null;
        if (c.SetParent) fields["parent"] = c.ParentKey is { } parent ? new { key = parent } : null;
        if (c.SetEstimate)
        {
            var points = (await tracker.FieldsAsync(ctx, ct)).StoryPointsId ?? throw new TrackerException(TrackerException.Codes.Rejected);
            fields[points] = decimal.TryParse(c.Estimate, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : null;
        }

        return fields;
    }

    private static Dictionary<string, object?> Body(IterationChange c)
    {
        var body = new Dictionary<string, object?>();
        if (c.Name is not null) body["name"] = c.Name;
        if (c.Goal is not null) body["goal"] = c.Goal;
        if (c.Start is { } s) body["startDate"] = Stamp(s);
        if (c.End is { } e) body["endDate"] = Stamp(e);
        return body;
    }

    private static string Stamp(DateOnly d) => d.ToString("yyyy-MM-dd'T'00:00:00.000'Z'", CultureInfo.InvariantCulture);

    /// <summary>The Jira type name for a Podium type; a spike is a task there.</summary>
    public static string TypeName(string podiumType) => podiumType switch
    {
        WorkItemTypes.Epic => "Epic",
        WorkItemTypes.Story => "Story",
        WorkItemTypes.Bug => "Bug",
        _ => "Task",
    };

    public static string? PriorityName(string? rank) => rank switch
    {
        "1" => "Highest",
        "2" => "High",
        "3" => "Medium",
        "4" => "Low",
        "5" => "Lowest",
        _ => null,
    };
}
