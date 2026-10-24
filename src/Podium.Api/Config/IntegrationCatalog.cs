namespace Podium.Api.Config;

/// <summary>The integrations the server's Settings → Integrations page manages (ADR-0017).</summary>
public static class IntegrationIds
{
    public const string Smtp = "smtp";
    public const string GitHub = "github";
    public const string Google = "google";
    public const string Jira = "jira";
    public static readonly IReadOnlyList<string> All = [Smtp, GitHub, Google, Jira];
}

/// <summary>
/// One setting of an integration: the name the page stores it under, the environment variable that wins over it (and the
/// "Smtp" node of a server-only appsettings, for SMTP), and whether it is a secret (sealed at rest, never sent back to the web).
/// </summary>
public sealed record SettingField(string Integration, string Name, string EnvName, bool Secret, string? SmtpNode = null)
{
    public string Key => $"{Integration}.{Name}";
}

/// <summary>Every setting the page knows, in the order the page shows them. The single list the config, the store and the page share.</summary>
public static class IntegrationCatalog
{
    public const string Host = "host";
    public const string Port = "port";
    public const string User = "user";
    public const string Password = "password";
    public const string From = "from";
    public const string Tls = "tls";
    public const string ClientId = "client_id";
    public const string ClientSecret = "client_secret";

    /// <summary>The longest value the page accepts.</summary>
    public const int ValueMax = 1000;

    public static readonly IReadOnlyList<SettingField> Fields =
    [
        new(IntegrationIds.Smtp, Host, "AIPOD_SMTP_HOST", false, "Host"),
        new(IntegrationIds.Smtp, Port, "AIPOD_SMTP_PORT", false, "Port"),
        new(IntegrationIds.Smtp, User, "AIPOD_SMTP_USER", false, "User"),
        new(IntegrationIds.Smtp, Password, "AIPOD_SMTP_PASSWORD", true, "Password"),
        new(IntegrationIds.Smtp, From, "AIPOD_SMTP_FROM", false, "From"),
        new(IntegrationIds.Smtp, Tls, "AIPOD_SMTP_TLS", false, "StartTls"),
        new(IntegrationIds.GitHub, ClientId, "AIPOD_GITHUB_CLIENT_ID", false),
        new(IntegrationIds.GitHub, ClientSecret, "AIPOD_GITHUB_CLIENT_SECRET", true),
        new(IntegrationIds.Google, ClientId, "AIPOD_GOOGLE_CLIENT_ID", false),
        new(IntegrationIds.Google, ClientSecret, "AIPOD_GOOGLE_CLIENT_SECRET", true),
        new(IntegrationIds.Jira, ClientId, "AIPOD_JIRA_CLIENT_ID", false),
        new(IntegrationIds.Jira, ClientSecret, "AIPOD_JIRA_CLIENT_SECRET", true),
    ];

    public static IEnumerable<SettingField> Of(string integration) => Fields.Where(f => f.Integration == integration);

    public static SettingField? Find(string integration, string name) =>
        Fields.FirstOrDefault(f => f.Integration == integration && f.Name == name);
}
