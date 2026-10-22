using System.Text.Json;
using Podium.Api.Config;
using Podium.Api.Data;

namespace Podium.Api.Integrations;

/// <summary>
/// Registers, extends and removes the dynamic webhooks of a Jira connection (ADR-0015 §6). Two registrations: issue events filtered to the
/// imported spaces, and sprint events. A site that cannot take them (the address is not public https, or Jira refuses) leaves the connection
/// working on the reconciliation pass alone; nothing here throws for that.
/// </summary>
public sealed class WebhookManager(ApiConfig config, JiraApi api, IntegrationTokens tokens, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan ExtendWhenLeft = TimeSpan.FromDays(20);
    private static readonly string[] IssueEvents = ["jira:issue_created", "jira:issue_updated", "jira:issue_deleted"];
    private static readonly string[] SprintEvents = ["sprint_created", "sprint_updated", "sprint_deleted", "sprint_started", "sprint_closed"];

    public string CallbackUrl(Guid connectionId) => $"{config.PublicOrigin}/api/integrations/jira/webhook/{connectionId}";

    /// <summary>Jira delivers only to a public https address.</summary>
    public bool CanRegister => config.PublicOrigin.StartsWith("https://", StringComparison.Ordinal);

    /// <summary>Makes the registrations match <paramref name="spaceKeys"/>: removed when none is imported, replaced when there are some.</summary>
    public async Task EnsureAsync(IntegrationConnection connection, IReadOnlyList<string> spaceKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (spaceKeys.Count == 0)
        {
            await TryRemoveAsync(connection, ct);
            Clear(connection);
            return;
        }

        if (!CanRegister || await tokens.ConnectorContextAsync(connection, ct) is not { } ctx) return;
        var wanted = string.Join(",", spaceKeys.Order(StringComparer.Ordinal));
        if (connection.WebhookIds is not null && connection.LastWebhookSpaces() == wanted) return;
        try
        {
            await TryRemoveAsync(connection, ct);
            var ids = new List<long>();
            foreach (var body in Bodies(connection, spaceKeys))
            {
                using var doc = await api.PostAsync(ctx, "/rest/api/3/webhook", body, ct);
                if (doc.RootElement.TryGetProperty("webhookRegistrationResult", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    ids.AddRange(results.EnumerateArray().Where(r => r.TryGetProperty("createdWebhookId", out var id) && id.TryGetInt64(out _))
                        .Select(r => r.GetProperty("createdWebhookId").GetInt64()));
                }
            }

            connection.WebhookIds = JsonSerializer.Serialize(new { ids, spaces = wanted });
            connection.WebhookExpiresAt = clock.GetUtcNow() + Lifetime;
        }
        catch (TrackerException)
        {
            Clear(connection); // webhook: off; the pass keeps the connection current
        }
    }

    /// <summary>Extends the registrations that are within <see cref="ExtendWhenLeft"/> of expiring; registers again when Jira no longer knows them.</summary>
    public async Task ExtendIfDueAsync(IntegrationConnection connection, IReadOnlyList<string> spaceKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.WebhookExpiresAt is not { } expires || connection.WebhookIds is null) return;
        if (expires - clock.GetUtcNow() > ExtendWhenLeft) return;
        var ids = connection.Ids();
        if (await tokens.ConnectorContextAsync(connection, ct) is not { } ctx) return;
        try
        {
            using var _ = await api.PutAsync(ctx, "/rest/api/3/webhook/refresh", new { webhookIds = ids }, ct);
            connection.WebhookExpiresAt = clock.GetUtcNow() + Lifetime;
        }
        catch (TrackerException)
        {
            // Jira may still hold them (a 503 says nothing about expiry): remove them first, then register afresh, so the 5-webhook limit is not eaten.
            await TryRemoveAsync(connection, ct);
            Clear(connection);
            await EnsureAsync(connection, spaceKeys, ct);
        }
    }

    public async Task TryRemoveAsync(IntegrationConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var ids = connection.Ids();
        if (ids.Count == 0 || await tokens.ConnectorContextAsync(connection, ct) is not { } ctx) return;
        try
        {
            using var _ = await api.SendAsync(ctx, HttpMethod.Delete, "/rest/api/3/webhook", new { webhookIds = ids }, ct);
        }
        catch (TrackerException)
        {
            // Jira expires an unextended webhook after 30 days anyway; a failed removal is not worth failing the caller.
        }
    }

    private static void Clear(IntegrationConnection connection)
    {
        connection.WebhookIds = null;
        connection.WebhookExpiresAt = null;
    }

    private IEnumerable<object> Bodies(IntegrationConnection connection, IReadOnlyList<string> spaceKeys)
    {
        var url = CallbackUrl(connection.Id);
        var jql = $"project in ({string.Join(", ", spaceKeys.Select(k => $"\"{k.Replace("\"", "", StringComparison.Ordinal)}\""))})";
        yield return new { url, webhooks = new[] { new { events = IssueEvents, jqlFilter = jql } } };
        yield return new { url, webhooks = new[] { new { events = SprintEvents } } };
    }
}

public static class ConnectionWebhooks
{
    public static List<long> Ids(this IntegrationConnection connection)
    {
        if (connection.WebhookIds is not { } json) return [];
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array
            ? [.. ids.EnumerateArray().Where(i => i.TryGetInt64(out _)).Select(i => i.GetInt64())]
            : [];
    }

    public static string? LastWebhookSpaces(this IntegrationConnection connection)
    {
        if (connection.WebhookIds is not { } json) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("spaces", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
    }
}
