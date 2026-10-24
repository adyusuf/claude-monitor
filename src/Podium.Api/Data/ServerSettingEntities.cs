namespace Podium.Api.Data;

// Server-wide settings saved on the Settings → Integrations page (ADR-0017, docs/data-model.md §9).

/// <summary>
/// One saved integration setting, by its catalog key ("smtp.password"). A secret's value is sealed with AIPOD_INTEGRATION_KEY and never
/// leaves the API; an environment variable for the same setting wins over the row.
/// </summary>
public sealed class ServerSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Secret { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid UpdatedBy { get; set; }
}
