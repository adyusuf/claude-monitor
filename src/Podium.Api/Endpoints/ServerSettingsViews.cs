using Podium.Api.Config;

namespace Podium.Api.Endpoints;

/// <summary>One setting as the page shows it. A secret's value is never sent: only whether it is set and where (ADR-0017). SavedHidden: a value saved on the page that the environment's hides.</summary>
public sealed record SettingFieldView(string Name, string EnvName, bool Secret, bool Required, string Source, string? Value, bool SavedHidden = false);

/// <summary>One integration: ready, incomplete (a required value is missing) or off (nothing set); the address to register at the provider.</summary>
public sealed record IntegrationSettingsView(string Id, string Status, string? CallbackUrl, IReadOnlyList<string> Notes,
    IReadOnlyList<SettingFieldView> Fields);

public sealed record ServerIntegrationsView(bool CanSave, IReadOnlyList<IntegrationSettingsView> Integrations);

/// <summary>The values to save for one integration: an empty value removes the saved one; a secret left out stays as it is.</summary>
public sealed record ServerSettingsRequest(Dictionary<string, string?>? Values, string? Code);

public static class IntegrationStatuses
{
    public const string Ready = "ready";
    public const string Incomplete = "incomplete";
    public const string Off = "off";
}

/// <summary>Builds the page's view from the running settings; no value of a secret passes through here.</summary>
public static class ServerSettingsViews
{
    public const string NoteIntegrationKey = "integration_key_missing";

    public static ServerIntegrationsView Of(IntegrationSettings settings, ApiConfig config) =>
        new(settings.CanSave, [.. IntegrationIds.All.Select(id => Integration(settings, config, id))]);

    private static IntegrationSettingsView Integration(IntegrationSettings settings, ApiConfig config, string id)
    {
        var snapshot = settings.Current;
        var fields = IntegrationCatalog.Of(id).Select(f =>
        {
            var value = Value(snapshot, config, f);
            var source = settings.Source(f);
            // A value saved on the page that the environment's now hides: it comes back if the environment's goes, so the page can remove it.
            var hidden = source == IntegrationSettings.SourceServer && snapshot.Page.ContainsKey(f.Key);
            return new SettingFieldView(f.Name, f.EnvName, f.Secret, Required(snapshot, f), source, f.Secret ? null : value, hidden);
        }).ToList();
        var given = fields.Count(f => f.Source != IntegrationSettings.SourceNone);
        var missing = fields.Any(f => f.Required && Value(snapshot, config, IntegrationCatalog.Find(id, f.Name)!) is null);
        var notes = new List<string>();
        if (id == IntegrationIds.Jira && config.IntegrationKey is null) notes.Add(NoteIntegrationKey);
        var status = given == 0 && id != IntegrationIds.Smtp ? IntegrationStatuses.Off
            : missing || notes.Count > 0 ? IntegrationStatuses.Incomplete
            : IntegrationStatuses.Ready;
        return new IntegrationSettingsView(id, status, CallbackUrl(config, id), notes, fields);
    }

    /// <summary>
    /// The value in force, or null when there is none. SMTP's host, port, sender and STARTTLS always have one (the environment is required
    /// for them outside development); the user name and password are required when STARTTLS is on, because a mail server over TLS asks for them.
    /// </summary>
    internal static string? Value(IntegrationSnapshot s, ApiConfig config, SettingField f)
    {
        if (config.IntegrationEnv.TryGetValue(f.Key, out var env)) return env;
        if (s.Page.TryGetValue(f.Key, out var saved)) return saved;
        return (f.Integration, f.Name) switch
        {
            (IntegrationIds.Smtp, IntegrationCatalog.Host) => s.Smtp.Host,
            (IntegrationIds.Smtp, IntegrationCatalog.Port) => s.Smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (IntegrationIds.Smtp, IntegrationCatalog.From) => s.Smtp.From,
            (IntegrationIds.Smtp, IntegrationCatalog.Tls) => s.Smtp.StartTls ? "true" : "false",
            (IntegrationIds.Smtp, IntegrationCatalog.User) => s.Smtp.User,
            (IntegrationIds.Smtp, IntegrationCatalog.Password) => s.Smtp.Password,
            _ => null,
        };
    }

    internal static bool Required(IntegrationSnapshot s, SettingField f) => (f.Integration, f.Name) switch
    {
        (IntegrationIds.Smtp, IntegrationCatalog.User or IntegrationCatalog.Password) => s.Smtp.StartTls,
        (IntegrationIds.Smtp, IntegrationCatalog.Tls) => false,
        _ => true,
    };

    internal static string? CallbackUrl(ApiConfig config, string id) => id switch
    {
        IntegrationIds.GitHub or IntegrationIds.Google => config.PublicOrigin + ProviderEndpoints.CallbackPrefix + id,
        IntegrationIds.Jira => config.PublicOrigin + Integrations.JiraOAuth.CallbackPath,
        _ => null,
    };
}
