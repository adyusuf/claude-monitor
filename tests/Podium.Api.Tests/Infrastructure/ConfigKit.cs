using Podium.Api.Config;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>An <see cref="ApiConfig"/> built from a few keys, without the host and without the process environment.</summary>
public static class ConfigKit
{
    /// <summary>The keys a production start needs (outside development nothing has a fallback).</summary>
    private static readonly Dictionary<string, string?> Required = new()
    {
        ["AIPOD_DB"] = "Host=db",
        ["AIPOD_PUBLIC_ORIGIN"] = "https://podium.test.invalid",
        ["AIPOD_ARCHIVE_DIR"] = "/var/archive",
        ["AIPOD_MFA_KEY"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        ["AIPOD_SMTP_HOST"] = "smtp.base.invalid",
        ["AIPOD_SMTP_PORT"] = "587",
        ["AIPOD_SMTP_FROM"] = "base@podium.test.invalid",
    };

    /// <summary>Development: every fallback applies, so a key is only what the test sets.</summary>
    public static ApiConfig Dev(params (string Key, string? Value)[] keys) => From(development: true, [], keys);

    /// <summary>Not development: the required keys are there, and nothing else unless the test adds it (no integration key, SMTP over TLS).</summary>
    public static ApiConfig Prod(params (string Key, string? Value)[] keys) => From(development: false, Required, keys);

    /// <summary>A 32-byte key in the base64url form the environment holds it in, every byte the same.</summary>
    public static string Key(byte fill) => Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The running settings over this config; nothing is read from a database unless the test gives the scopes of one.</summary>
    public static IntegrationSettings Settings(ApiConfig config, IServiceScopeFactory? scopes = null, IOptionsMonitorCache<OAuthOptions>? cache = null,
        ILogger<IntegrationSettings>? log = null) =>
        new(config, scopes ?? new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            cache ?? new OptionsCache<OAuthOptions>(), log ?? NullLogger<IntegrationSettings>.Instance);

    private static ApiConfig From(bool development, Dictionary<string, string?> baseKeys, (string Key, string? Value)[] keys)
    {
        var all = new Dictionary<string, string?>(baseKeys);
        foreach (var (key, value) in keys) all[key] = value;
        return ApiConfig.From(new ConfigurationBuilder().AddInMemoryCollection(all).Build(), development);
    }
}
