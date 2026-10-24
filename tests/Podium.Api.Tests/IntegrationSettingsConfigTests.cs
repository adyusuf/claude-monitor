using Podium.Api.Config;
using Podium.Api.Data;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Podium.Api.Tests;

/// <summary>What the environment gives the integration settings (ADR-0017): the admin workspace, the environment's values and the running settings built from them. No host.</summary>
public sealed class IntegrationSettingsConfigTests
{
    private static readonly Guid Admin = Guid.Parse("0d1c2b3a-4f5e-4a6b-8c7d-9e0f1a2b3c4d");

    // ---- AIPOD_ADMIN_WORKSPACE ------------------------------------------------------------------------------------

    [Fact]
    public void The_admin_workspace_is_the_id_in_the_environment()
    {
        Assert.Equal(Admin, ConfigKit.Dev(("AIPOD_ADMIN_WORKSPACE", Admin.ToString())).AdminWorkspace);
        Assert.Equal(Admin, ConfigKit.Prod(("AIPOD_ADMIN_WORKSPACE", Admin.ToString("N"))).AdminWorkspace);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_an_admin_workspace_nobody_manages_the_server_settings(string? value) =>
        Assert.Null(ConfigKit.Dev(("AIPOD_ADMIN_WORKSPACE", value)).AdminWorkspace);

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData(" ")]
    [InlineData("0d1c2b3a-4f5e-4a6b-8c7d")]
    public void An_admin_workspace_that_is_not_an_id_stops_the_start_and_names_the_variable(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ConfigKit.Dev(("AIPOD_ADMIN_WORKSPACE", value)));
        Assert.Contains("AIPOD_ADMIN_WORKSPACE", error.Message, StringComparison.Ordinal);
    }

    // ---- the environment's values ---------------------------------------------------------------------------------

    [Fact]
    public void Only_the_non_empty_values_of_the_environment_count_and_each_is_keyed_by_its_catalog_key()
    {
        var config = ConfigKit.Dev(("AIPOD_SMTP_HOST", "smtp.env.invalid"), ("AIPOD_SMTP_USER", ""), ("AIPOD_GITHUB_CLIENT_ID", "gh"),
            ("AIPOD_GITHUB_CLIENT_SECRET", "ghs"), ("AIPOD_JIRA_CLIENT_ID", "jira-only-id"));

        Assert.Equal(new SortedDictionary<string, string>
        {
            ["github.client_id"] = "gh",
            ["github.client_secret"] = "ghs",
            ["jira.client_id"] = "jira-only-id",
            ["smtp.host"] = "smtp.env.invalid",
        }, new SortedDictionary<string, string>(config.IntegrationEnv.ToDictionary(p => p.Key, p => p.Value)));
    }

    [Fact]
    public void The_mail_settings_of_the_Smtp_node_count_as_the_environment_and_the_key_wins_over_the_node()
    {
        var config = ConfigKit.Dev(("Smtp:Host", "node.invalid"), ("Smtp:User", "node-user"), ("AIPOD_SMTP_HOST", "key.invalid"));

        Assert.Equal("key.invalid", config.IntegrationEnv["smtp.host"]);
        Assert.Equal("node-user", config.IntegrationEnv["smtp.user"]);
        Assert.False(config.IntegrationEnv.ContainsKey("smtp.password"));
    }

    [Fact]
    public void Nothing_in_the_environment_leaves_the_environment_empty()
    {
        Assert.Empty(ConfigKit.Dev().IntegrationEnv);
    }

    // ---- the running settings with nothing saved -----------------------------------------------------------------

    [Fact]
    public void An_oauth_app_is_in_force_only_when_both_its_id_and_its_secret_are_set()
    {
        var settings = ConfigKit.Settings(ConfigKit.Dev(("AIPOD_GITHUB_CLIENT_ID", "gh"), ("AIPOD_GITHUB_CLIENT_SECRET", "ghs"),
            ("AIPOD_GOOGLE_CLIENT_ID", "only-an-id"), ("AIPOD_JIRA_CLIENT_SECRET", "only-a-secret")));

        Assert.Equal(new OAuthClient("gh", "ghs"), settings.Provider(Providers.GitHub));
        Assert.Null(settings.Provider(Providers.Google));
        Assert.Null(settings.Jira);
        Assert.Null(settings.Provider("jira")); // not a sign-in provider
        Assert.Null(settings.Provider("myspace"));
    }

    [Fact]
    public void The_mail_settings_in_force_are_the_environments_and_the_rest_the_development_fallback()
    {
        var settings = ConfigKit.Settings(ConfigKit.Dev(("AIPOD_SMTP_HOST", "smtp.env.invalid"), ("AIPOD_SMTP_PORT", "2525"), ("AIPOD_SMTP_TLS", "TRUE")));

        Assert.Equal(new SmtpSettings("smtp.env.invalid", 2525, null, null, "podium@localhost", true), settings.Smtp);
        Assert.Equal(new SmtpSettings("localhost", 1025, null, null, "podium@localhost", false), ConfigKit.Settings(ConfigKit.Dev()).Smtp);
    }

    [Fact]
    public void Each_setting_says_where_it_comes_from_and_saving_needs_the_integration_key()
    {
        var withKey = ConfigKit.Settings(ConfigKit.Dev(("AIPOD_SMTP_HOST", "smtp.env.invalid")));
        var withoutKey = ConfigKit.Settings(ConfigKit.Prod());

        Assert.Equal("server", withKey.Source(IntegrationCatalog.Find("smtp", "host")!));
        Assert.Equal("none", withKey.Source(IntegrationCatalog.Find("smtp", "user")!));
        Assert.True(withKey.CanSave);
        Assert.False(withoutKey.CanSave);
        Assert.False(ConfigKit.Settings(ConfigKit.Prod(("AIPOD_JIRA_CLIENT_ID", "id"), ("AIPOD_JIRA_CLIENT_SECRET", "secret"))).JiraAvailable);
        Assert.True(ConfigKit.Settings(ConfigKit.Prod(("AIPOD_JIRA_CLIENT_ID", "id"), ("AIPOD_JIRA_CLIENT_SECRET", "secret"),
            ("AIPOD_INTEGRATION_KEY", ConfigKit.Key(7)))).JiraAvailable);
        Assert.False(ConfigKit.Settings(ConfigKit.Dev()).JiraAvailable); // a key (the development one) but no app
    }

    // ---- the OAuth schemes: always registered, the app read from the running settings -----------------------------

    /// <summary>What an unset provider's options hold: the handler validates them on every request, so they are never empty.</summary>
    internal const string Placeholder = "not-configured";

    private static ServiceProvider Schemes(IntegrationSettings settings, ApiConfig config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton(config);
        services.AddSingleton(settings);
        services.AddAuthentication().AddExternalProviders(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Both_sign_in_schemes_exist_with_their_app_when_it_is_set_and_with_a_placeholder_when_it_is_not()
    {
        var config = ConfigKit.Dev(("AIPOD_GITHUB_CLIENT_ID", "gh-id"), ("AIPOD_GITHUB_CLIENT_SECRET", "gh-secret"));
        using var sp = Schemes(ConfigKit.Settings(config), config);
        var options = sp.GetRequiredService<IOptionsMonitor<OAuthOptions>>();

        var github = options.Get(Providers.GitHub);
        var google = options.Get(Providers.Google);

        Assert.Equal(("gh-id", "gh-secret"), (github.ClientId, github.ClientSecret));
        Assert.Equal((Placeholder, Placeholder), (google.ClientId, google.ClientSecret));
        Assert.Equal("/api/auth/callback/google", google.CallbackPath.Value);
        Assert.Equal("/api/auth/callback/github", github.CallbackPath.Value);
    }
}
