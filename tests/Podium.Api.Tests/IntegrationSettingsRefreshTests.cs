using System.Text;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Podium.Api.Tests;

/// <summary>Reading the saved rows again (ADR-0017): the environment wins over the page, secrets are opened or left out, the OAuth options are dropped when an app changes.</summary>
[Collection(ApiGroup.Name)]
public sealed class IntegrationSettingsRefreshTests(ApiFactory api) : IAsyncLifetime
{
    public Task InitializeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    public Task DisposeAsync() => ServerAdminKit.ResetSettingsAsync(api);

    private sealed class ListLogger : ILogger<IntegrationSettings>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    /// <summary>Settings of their own over the factory's database, so a test chooses the environment and the key.</summary>
    private IntegrationSettings Settings(ApiConfig config, OptionsCache<OAuthOptions>? cache = null, ILogger<IntegrationSettings>? log = null) =>
        ConfigKit.Settings(config, api.Services.GetRequiredService<IServiceScopeFactory>(), cache, log);

    private async Task SaveAsync(TestUser by, params (string Key, string Value, bool Sealed)[] rows)
    {
        var key = ConfigKit.Dev().IntegrationKey!; // the key the rows are sealed with unless a test seals them itself
        await using var db = api.Db();
        foreach (var (name, value, isSealed) in rows)
        {
            db.ServerSettings.Add(new ServerSetting
            {
                Key = name,
                Value = isSealed ? SecretBox.Seal(key, Encoding.UTF8.GetBytes(value)) : value,
                Secret = isSealed,
                UpdatedAt = api.Clock.GetUtcNow(),
                UpdatedBy = by.Id,
            });
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_environment_wins_over_a_saved_value_and_a_saved_value_over_the_fallback()
    {
        var by = await api.NewClient().SignedUpAsync("rf-order");
        var from = Emails.New("rf-from");
        await SaveAsync(by, ("smtp.host", "page.example.net", false), ("smtp.port", "2526", false), ("smtp.user", "page-user", false),
            ("smtp.password", "page-pw", true), ("smtp.from", from, false), ("smtp.tls", "true", false));
        var settings = Settings(ConfigKit.Dev(("AIPOD_SMTP_HOST", "env.example.net")));

        await settings.RefreshAsync(CancellationToken.None);

        Assert.Equal(new SmtpSettings("env.example.net", 2526, "page-user", "page-pw", from, true), settings.Smtp);
        Assert.Equal(["server", "page", "page", "page", "page", "page"],
            IntegrationCatalog.Of("smtp").Select(f => settings.Source(f)));
    }

    [Fact]
    public async Task A_removed_row_gives_the_fallback_back_at_the_next_read()
    {
        var by = await api.NewClient().SignedUpAsync("rf-gone");
        await SaveAsync(by, ("smtp.host", "page.example.net", false), ("smtp.password", "page-pw", true));
        var settings = Settings(ConfigKit.Dev());
        await settings.RefreshAsync(CancellationToken.None);
        Assert.Equal(("page.example.net", "page-pw"), (settings.Smtp.Host, settings.Smtp.Password));

        await ServerAdminKit.ResetSettingsAsync(api);
        await settings.RefreshAsync(CancellationToken.None);

        Assert.Equal(("localhost", (string?)null), (settings.Smtp.Host, settings.Smtp.Password));
        Assert.Equal("none", settings.Source(IntegrationCatalog.Find("smtp", "host")!));
    }

    [Theory]
    [InlineData("abc", 1025)]
    [InlineData("+25", 1025)]
    [InlineData("-25", 1025)]
    [InlineData("", 1025)]
    [InlineData("2525", 2525)]
    public async Task A_saved_port_that_is_not_a_plain_number_is_ignored(string saved, int port)
    {
        var by = await api.NewClient().SignedUpAsync("rf-port");
        await SaveAsync(by, ("smtp.port", saved, false));
        var settings = Settings(ConfigKit.Dev()); // the fallback here: port 1025

        await settings.RefreshAsync(CancellationToken.None);

        Assert.Equal(port, settings.Smtp.Port);
    }

    [Theory]
    [InlineData("yes", false)]
    [InlineData("1", false)]
    [InlineData("TRUE", true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task A_saved_tls_is_on_only_when_it_says_true(string saved, bool tls)
    {
        var by = await api.NewClient().SignedUpAsync("rf-tls");
        await SaveAsync(by, ("smtp.tls", saved, false));
        var settings = Settings(ConfigKit.Prod()); // outside development STARTTLS is on unless the environment says otherwise

        await settings.RefreshAsync(CancellationToken.None);

        Assert.Equal(tls, settings.Smtp.StartTls);
        Assert.True(ConfigKit.Settings(ConfigKit.Prod()).Smtp.StartTls);
    }

    [Fact]
    public async Task An_oauth_app_is_in_force_only_when_both_halves_are_there_and_each_half_follows_the_same_order()
    {
        var by = await api.NewClient().SignedUpAsync("rf-oauth");
        await SaveAsync(by, ("github.client_id", "page-gh-id", false), ("google.client_id", "page-g-id", false), ("google.client_secret", "page-g-secret", true));
        var settings = Settings(ConfigKit.Dev(("AIPOD_GOOGLE_CLIENT_ID", "env-g-id")));

        await settings.RefreshAsync(CancellationToken.None);

        Assert.Null(settings.Provider(Providers.GitHub)); // an id alone is no app
        Assert.Equal(new OAuthClient("env-g-id", "page-g-secret"), settings.Provider(Providers.Google));
        await SaveAsync(by, ("github.client_secret", "page-gh-secret", true));
        await settings.RefreshAsync(CancellationToken.None);
        Assert.Equal(new OAuthClient("page-gh-id", "page-gh-secret"), settings.Provider(Providers.GitHub));
    }

    [Fact]
    public async Task A_secret_that_cannot_be_opened_is_left_out_and_the_log_names_the_setting_never_the_value()
    {
        var by = await api.NewClient().SignedUpAsync("rf-sealed");
        await SaveAsync(by, ("smtp.password", "page-pw", true), ("smtp.user", "plain-user", false), ("smtp.host", "page.example.net", false));
        var wrongLog = new ListLogger();
        var wrong = Settings(ConfigKit.Dev(("AIPOD_INTEGRATION_KEY", ConfigKit.Key(9))), log: wrongLog);
        var noKeyLog = new ListLogger();
        var noKey = Settings(ConfigKit.Prod(), log: noKeyLog);

        await wrong.RefreshAsync(CancellationToken.None);
        await noKey.RefreshAsync(CancellationToken.None);

        Assert.Equal((null, "plain-user", "page.example.net"), (wrong.Smtp.Password, wrong.Smtp.User, wrong.Smtp.Host));
        Assert.Equal((null, "plain-user", "smtp.base.invalid"), (noKey.Smtp.Password, noKey.Smtp.User, noKey.Smtp.Host)); // the host comes from its environment
        Assert.Equal("none", wrong.Source(IntegrationCatalog.Find("smtp", "password")!));
        var line = Assert.Single(wrongLog.Lines);
        Assert.Contains("smtp.password", line, StringComparison.Ordinal);
        Assert.Contains("AIPOD_INTEGRATION_KEY", Assert.Single(noKeyLog.Lines), StringComparison.Ordinal);
        Assert.All(wrongLog.Lines.Concat(noKeyLog.Lines), l => Assert.DoesNotContain("page-pw", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_row_that_is_not_a_sealed_value_is_left_out_without_stopping_the_read()
    {
        var by = await api.NewClient().SignedUpAsync("rf-garbage");
        await using (var db = api.Db())
        {
            db.ServerSettings.Add(new ServerSetting { Key = "smtp.password", Value = "***not base64***", Secret = true, UpdatedAt = api.Clock.GetUtcNow(), UpdatedBy = by.Id });
            db.ServerSettings.Add(new ServerSetting { Key = "smtp.host", Value = "page.example.net", UpdatedAt = api.Clock.GetUtcNow(), UpdatedBy = by.Id });
            await db.SaveChangesAsync();
        }

        var settings = Settings(ConfigKit.Dev());
        await settings.RefreshAsync(CancellationToken.None);

        Assert.Equal((null, "page.example.net"), (settings.Smtp.Password, settings.Smtp.Host));
    }

    [Fact]
    public async Task A_sealed_value_shorter_than_a_nonce_and_tag_is_left_out_and_the_server_still_starts()
    {
        var by = await api.NewClient().SignedUpAsync("rf-short");
        await using (var db = api.Db())
        {
            // "AAAA" is valid base64 for 3 bytes: less than the 28 that nonce and tag take.
            db.ServerSettings.Add(new ServerSetting { Key = "smtp.password", Value = "AAAA", Secret = true, UpdatedAt = api.Clock.GetUtcNow(), UpdatedBy = by.Id });
            db.ServerSettings.Add(new ServerSetting { Key = "smtp.host", Value = "page.example.net", UpdatedAt = api.Clock.GetUtcNow(), UpdatedBy = by.Id });
            await db.SaveChangesAsync();
        }

        var settings = Settings(ConfigKit.Dev());
        await settings.RefreshAsync(CancellationToken.None);

        Assert.Equal((null, "page.example.net"), (settings.Smtp.Password, settings.Smtp.Host));
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => Podium.Api.Security.SecretBox.Open(new byte[32], "AAAA"));
    }

    [Fact]
    public async Task The_cached_options_of_a_sign_in_provider_are_dropped_only_when_its_app_changed()
    {
        var by = await api.NewClient().SignedUpAsync("rf-cache");
        var cache = new OptionsCache<OAuthOptions>();
        var settings = Settings(ConfigKit.Dev(), cache);
        cache.GetOrAdd(Providers.GitHub, () => new OAuthOptions());
        cache.GetOrAdd(Providers.Google, () => new OAuthOptions());

        await settings.RefreshAsync(CancellationToken.None); // nothing changed
        Assert.False(cache.TryAdd(Providers.GitHub, new OAuthOptions()));
        Assert.False(cache.TryAdd(Providers.Google, new OAuthOptions()));

        await SaveAsync(by, ("github.client_id", "gh", false), ("github.client_secret", "ghs", true));
        await settings.RefreshAsync(CancellationToken.None); // GitHub's app appeared
        Assert.True(cache.TryAdd(Providers.GitHub, new OAuthOptions())); // it was dropped
        Assert.False(cache.TryAdd(Providers.Google, new OAuthOptions())); // Google's stayed

        cache.TryRemove(Providers.GitHub);
        cache.TryAdd(Providers.GitHub, new OAuthOptions());
        await settings.RefreshAsync(CancellationToken.None); // nothing changed again
        Assert.False(cache.TryAdd(Providers.GitHub, new OAuthOptions()));
    }
}
