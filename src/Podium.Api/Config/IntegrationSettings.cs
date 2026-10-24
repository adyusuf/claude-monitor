using System.Globalization;
using System.Security.Cryptography;
using Podium.Api.Data;
using Podium.Api.Security;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Podium.Api.Config;

/// <summary>The integration values in force: the environment's, else the page's, else (SMTP only) the development fallback.</summary>
public sealed record IntegrationSnapshot(SmtpSettings Smtp, OAuthClient? GitHub, OAuthClient? Google, OAuthClient? Jira,
    IReadOnlyDictionary<string, string> Page);

/// <summary>
/// The running integration settings (ADR-0017). Built from <see cref="ApiConfig"/> and the server_settings rows, re-read at start, once a minute and
/// right after a save, so a value saved on the page takes effect without a restart. A non-empty environment value always wins over a saved one.
/// </summary>
public sealed class IntegrationSettings(ApiConfig config, IServiceScopeFactory scopes, IOptionsMonitorCache<OAuthOptions> oauth,
    ILogger<IntegrationSettings> log) : IDisposable
{
    public const string SourceServer = "server";
    public const string SourcePage = "page";
    public const string SourceNone = "none";

    private readonly SemaphoreSlim refreshing = new(1, 1);
    private volatile IntegrationSnapshot current = Compose(config, new Dictionary<string, string>());

    public IntegrationSnapshot Current => current;
    public SmtpSettings Smtp => current.Smtp;
    public OAuthClient? Jira => current.Jira;

    /// <summary>Jira is offered only when its app is set (here or in the environment) and tokens can be sealed.</summary>
    public bool JiraAvailable => current.Jira is not null && config.IntegrationKey is not null;

    /// <summary>Whether the page can save: secrets are sealed with AIPOD_INTEGRATION_KEY, so without it nothing is saved.</summary>
    public bool CanSave => config.IntegrationKey is not null;

    /// <summary>The OAuth app of a sign-in provider, or null when it is not offered.</summary>
    public OAuthClient? Provider(string provider) => provider switch
    {
        Providers.GitHub => current.GitHub,
        Providers.Google => current.Google,
        _ => null,
    };

    public string Source(SettingField field) =>
        config.IntegrationEnv.ContainsKey(field.Key) ? SourceServer : current.Page.ContainsKey(field.Key) ? SourcePage : SourceNone;

    /// <summary>Reads the saved rows again. A row that cannot be opened (no key, another key) is left out and named in the log, never its value.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        // One at a time: a slow read that began before a save must not assign its older rows after the save's own refresh.
        await refreshing.WaitAsync(ct);
        try
        {
            await ReadAsync(ct);
        }
        finally
        {
            refreshing.Release();
        }
    }

    public void Dispose() => refreshing.Dispose();

    private async Task ReadAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PodiumDb>();
        var rows = await db.ServerSettings.AsNoTracking().ToListAsync(ct);
        var page = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!row.Secret)
            {
                page[row.Key] = row.Value;
                continue;
            }

            if (config.IntegrationKey is null)
            {
                log.LogWarning("Saved setting {Key} is sealed but AIPOD_INTEGRATION_KEY is not set: left out", row.Key);
                continue;
            }

            try
            {
                page[row.Key] = System.Text.Encoding.UTF8.GetString(SecretBox.Open(config.IntegrationKey, row.Value));
            }
            catch (Exception e) when (e is CryptographicException or FormatException)
            {
                log.LogWarning("Saved setting {Key} could not be opened with AIPOD_INTEGRATION_KEY: left out", row.Key);
            }
        }

        var next = Compose(config, page);
        var before = current;
        current = next;
        // The OAuth handlers keep their options; dropping them makes the next sign-in build them again from the new values.
        if (before.GitHub != next.GitHub) oauth.TryRemove(Providers.GitHub);
        if (before.Google != next.Google) oauth.TryRemove(Providers.Google);
    }

    internal static IntegrationSnapshot Compose(ApiConfig config, IReadOnlyDictionary<string, string> page)
    {
        string? Value(string integration, string name)
        {
            var key = $"{integration}.{name}";
            return config.IntegrationEnv.TryGetValue(key, out var env) ? env : page.TryGetValue(key, out var saved) ? saved : null;
        }

        OAuthClient? Client(string integration) =>
            Value(integration, IntegrationCatalog.ClientId) is { } id && Value(integration, IntegrationCatalog.ClientSecret) is { } secret
                ? new OAuthClient(id, secret)
                : null;

        var fallback = config.Smtp;
        var port = int.TryParse(Value(IntegrationIds.Smtp, IntegrationCatalog.Port), NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : fallback.Port;
        var tls = Value(IntegrationIds.Smtp, IntegrationCatalog.Tls) is { } t ? string.Equals(t, "true", StringComparison.OrdinalIgnoreCase) : fallback.StartTls;
        var smtp = new SmtpSettings(
            Value(IntegrationIds.Smtp, IntegrationCatalog.Host) ?? fallback.Host,
            port,
            Value(IntegrationIds.Smtp, IntegrationCatalog.User) ?? fallback.User,
            Value(IntegrationIds.Smtp, IntegrationCatalog.Password) ?? fallback.Password,
            Value(IntegrationIds.Smtp, IntegrationCatalog.From) ?? fallback.From,
            tls);
        return new IntegrationSnapshot(smtp, Client(IntegrationIds.GitHub), Client(IntegrationIds.Google), Client(IntegrationIds.Jira), page);
    }
}

/// <summary>Reads the saved integration settings before the server takes requests, then once a minute (another instance may have saved).</summary>
public sealed class IntegrationSettingsLoader(IntegrationSettings settings, ILogger<IntegrationSettingsLoader> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) await LoadAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    internal async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            await settings.RefreshAsync(ct);
        }
        catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException)
        {
            // Before the first migration the table is not there yet: the environment's values stay in force.
            log.LogWarning("Saved integration settings could not be read: {Type}", e.GetType().Name);
        }
    }
}
