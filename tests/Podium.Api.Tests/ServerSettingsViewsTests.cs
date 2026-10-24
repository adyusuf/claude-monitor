using System.Text.Json;
using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Podium.Api.Tests;

/// <summary>The page's view built from the running settings (ADR-0017), and the loader that reads the saved rows. No host.</summary>
public sealed class ServerSettingsViewsTests
{
    private static ServerIntegrationsView View(ApiConfig config) => ServerSettingsViews.Of(ConfigKit.Settings(config), config);

    private static IntegrationSettingsView Of(ServerIntegrationsView view, string id) => view.Integrations.Single(i => i.Id == id);

    private static SettingFieldView Field(ServerIntegrationsView view, string id, string name) => Of(view, id).Fields.Single(f => f.Name == name);

    [Fact]
    public void With_nothing_set_the_apps_are_off_and_the_mail_is_ready_on_its_development_values()
    {
        var view = View(ConfigKit.Dev());

        Assert.True(view.CanSave);
        Assert.Equal(["smtp", "github", "google", "jira"], view.Integrations.Select(i => i.Id));
        Assert.Equal(["ready", "off", "off", "off"], view.Integrations.Select(i => i.Status));
        Assert.All(view.Integrations, i => Assert.Empty(i.Notes));
        Assert.Equal(["host", "port", "user", "password", "from", "tls"], Of(view, "smtp").Fields.Select(f => f.Name));
        Assert.Equal(["client_id", "client_secret"], Of(view, "github").Fields.Select(f => f.Name));
        Assert.All(view.Integrations.SelectMany(i => i.Fields), f => Assert.Equal("none", f.Source));
        Assert.Equal(("localhost", "1025", "podium@localhost", "false"),
            (Field(view, "smtp", "host").Value, Field(view, "smtp", "port").Value, Field(view, "smtp", "from").Value, Field(view, "smtp", "tls").Value));
    }

    [Fact]
    public void An_app_with_only_its_id_is_incomplete_and_with_both_it_is_ready_and_its_secret_value_is_never_in_the_view()
    {
        var idOnly = View(ConfigKit.Dev(("AIPOD_GITHUB_CLIENT_ID", "gh-id")));
        var both = View(ConfigKit.Dev(("AIPOD_GITHUB_CLIENT_ID", "gh-id"), ("AIPOD_GITHUB_CLIENT_SECRET", "gh-secret-value")));

        Assert.Equal("incomplete", Of(idOnly, "github").Status);
        Assert.Equal("ready", Of(both, "github").Status);
        Assert.Equal(("server", "gh-id", false), (Field(both, "github", "client_id").Source, Field(both, "github", "client_id").Value, Field(both, "github", "client_id").Secret));
        Assert.Equal(("server", null, true, true), (Field(both, "github", "client_secret").Source, Field(both, "github", "client_secret").Value,
            Field(both, "github", "client_secret").Secret, Field(both, "github", "client_secret").Required));
        Assert.Equal("AIPOD_GITHUB_CLIENT_SECRET", Field(both, "github", "client_secret").EnvName);
        Assert.DoesNotContain("gh-secret-value", JsonSerializer.Serialize(both), StringComparison.Ordinal);
    }

    [Fact]
    public void Jira_without_the_integration_key_says_so_is_incomplete_and_cannot_be_saved()
    {
        var config = ConfigKit.Prod(("AIPOD_JIRA_CLIENT_ID", "j-id"), ("AIPOD_JIRA_CLIENT_SECRET", "j-secret"));

        var view = View(config);

        Assert.False(view.CanSave);
        Assert.Equal(IntegrationStatuses.Incomplete, Of(view, "jira").Status);
        Assert.Equal(["integration_key_missing"], Of(view, "jira").Notes);
        // Only Jira seals tokens: the note is Jira's own, even when no app is set yet (then it is also off).
        Assert.Empty(Of(view, "github").Notes);
        var noApp = View(ConfigKit.Prod());
        Assert.Equal(IntegrationStatuses.Off, Of(noApp, "jira").Status);
        Assert.Equal(["integration_key_missing"], Of(noApp, "jira").Notes);
        Assert.Equal(IntegrationStatuses.Ready, Of(View(ConfigKit.Prod(("AIPOD_JIRA_CLIENT_ID", "j-id"), ("AIPOD_JIRA_CLIENT_SECRET", "j-secret"),
            ("AIPOD_INTEGRATION_KEY", ConfigKit.Key(3)))), "jira").Status);
    }

    [Fact]
    public void Mail_over_TLS_needs_a_user_and_a_password_and_without_TLS_it_does_not()
    {
        var tls = View(ConfigKit.Prod()); // outside development STARTTLS is on unless the environment says otherwise
        var withUser = View(ConfigKit.Prod(("AIPOD_SMTP_USER", "mailer"), ("AIPOD_SMTP_PASSWORD", "pw-from-env")));
        var plain = View(ConfigKit.Prod(("AIPOD_SMTP_TLS", "false")));

        Assert.Equal(("incomplete", true, true), (Of(tls, "smtp").Status, Field(tls, "smtp", "user").Required, Field(tls, "smtp", "password").Required));
        Assert.Equal(("ready", "server", "server"), (Of(withUser, "smtp").Status, Field(withUser, "smtp", "user").Source, Field(withUser, "smtp", "password").Source));
        Assert.Equal(("ready", false, false, false), (Of(plain, "smtp").Status, Field(plain, "smtp", "user").Required, Field(plain, "smtp", "password").Required,
            Field(plain, "smtp", "tls").Required));
        Assert.Null(Field(withUser, "smtp", "password").Value);
        Assert.Equal("mailer", Field(withUser, "smtp", "user").Value);
    }

    [Fact]
    public void The_callback_addresses_are_under_the_public_origin_and_only_the_apps_have_one()
    {
        var view = View(ConfigKit.Prod(("AIPOD_PUBLIC_ORIGIN", "https://podium.test.invalid/")));

        Assert.Equal([null, "https://podium.test.invalid/api/auth/callback/github", "https://podium.test.invalid/api/auth/callback/google",
            "https://podium.test.invalid/api/integrations/jira/callback"], view.Integrations.Select(i => i.CallbackUrl));
    }

    // ---- the loader ----------------------------------------------------------------------------------------------

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    private sealed class FailingScopes(Exception error) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw error;
    }

    private static async Task<IntegrationSettings> LoadedAsync(IServiceScopeFactory scopes, ListLogger<IntegrationSettingsLoader> log)
    {
        var settings = ConfigKit.Settings(ConfigKit.Dev(("AIPOD_SMTP_HOST", "env.example.net")), scopes);
        using var loader = new IntegrationSettingsLoader(settings, log);
        await loader.StartAsync(CancellationToken.None);
        await loader.StopAsync(CancellationToken.None);
        return settings;
    }

    [Fact]
    public async Task A_database_that_cannot_be_reached_leaves_the_environments_values_in_force_and_logs_only_the_kind_of_error()
    {
        var services = new ServiceCollection();
        services.AddDbContext<PodiumDb>(o => o.UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=unreachable-pw;Timeout=3"));
        using var provider = services.BuildServiceProvider();
        var log = new ListLogger<IntegrationSettingsLoader>();

        var settings = await LoadedAsync(provider.GetRequiredService<IServiceScopeFactory>(), log);

        Assert.Equal("env.example.net", settings.Smtp.Host);
        var line = Assert.Single(log.Lines);
        Assert.Contains("Exception", line, StringComparison.Ordinal); // the kind of error, nothing of its message
        Assert.DoesNotContain("unreachable-pw", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scope_that_cannot_give_the_database_is_swallowed_as_well_but_any_other_failure_is_not()
    {
        var empty = new ServiceCollection().BuildServiceProvider(); // no PodiumDb at all: the scope cannot give one
        var log = new ListLogger<IntegrationSettingsLoader>();

        var settings = await LoadedAsync(empty.GetRequiredService<IServiceScopeFactory>(), log);

        Assert.Equal("env.example.net", settings.Smtp.Host);
        Assert.Contains("InvalidOperationException", Assert.Single(log.Lines), StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => LoadedAsync(new FailingScopes(new ArgumentException("boom")), new ListLogger<IntegrationSettingsLoader>()));
        Assert.Equal(TimeSpan.FromMinutes(1), IntegrationSettingsLoader.Every);
    }
}
