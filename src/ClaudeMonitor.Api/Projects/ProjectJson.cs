using System.Text.Json;

namespace ClaudeMonitor.Api.Projects;

/// <summary>The jsonb columns of projects and applications: a list written as camelCase JSON, an empty list stored as NULL.</summary>
internal static class ProjectJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string? Pack<T>(IReadOnlyCollection<T>? items) => items is null or { Count: 0 } ? null : JsonSerializer.Serialize(items, Options);

    public static IReadOnlyList<T> Unpack<T>(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        // Only this API writes the column, always from a validated list.
        return JsonSerializer.Deserialize<List<T>>(json, Options) ?? [];
    }
}
