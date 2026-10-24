using Podium.Api.Config;
using Podium.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>
/// A second API host over the same database whose running configuration is changed: the one <see cref="ApiConfig"/> in the services is
/// replaced, so every service that takes it (the settings, the endpoints, the sign-in providers) sees the change.
/// </summary>
public static class HostKit
{
    public static WebApplicationFactory<Program> WithConfig(ApiFactory api, Func<ApiConfig, ApiConfig> change) =>
        api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(change(api.Services.GetRequiredService<ApiConfig>()))));

    /// <summary>A host whose database contexts carry this interceptor (and, when given, whose logs go to this log, every level).</summary>
    public static WebApplicationFactory<Program> WithInterceptor(ApiFactory api, IInterceptor interceptor, TracingLog? log = null) =>
        api.WithWebHostBuilder(b =>
        {
            b.ConfigureTestServices(s => s.ConfigureDbContext<PodiumDb>(o => o.AddInterceptors(interceptor)));
            if (log is not null)
            {
                b.ConfigureLogging(l =>
                {
                    l.AddProvider(log);
                    l.AddFilter<TracingLog>(null, LogLevel.Trace);
                });
            }
        });

    /// <summary>The configuration without the environment's values of this integration (an app that was never set in the environment).</summary>
    public static ApiConfig WithoutEnvOf(ApiConfig config, string integration) => config with
    {
        IntegrationEnv = config.IntegrationEnv.Where(e => !e.Key.StartsWith(integration + ".", StringComparison.Ordinal))
            .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),
    };

    /// <summary>The configuration with one more value from the environment.</summary>
    public static ApiConfig WithEnv(ApiConfig config, string key, string value) => config with
    {
        IntegrationEnv = new Dictionary<string, string>(config.IntegrationEnv, StringComparer.Ordinal) { [key] = value },
    };

    /// <summary>The same person, signed in on that host (sessions live in the database, so any host accepts the sign-in).</summary>
    public static async Task<TestUser> SignInAsync(ApiFactory api, WebApplicationFactory<Program> host, TestUser who)
    {
        ArgumentNullException.ThrowIfNull(who);
        var client = JiraKit.ClientOf(api, host);
        (await client.PostAsync("/api/auth/login", new { email = who.Email, password = TestUser.Password })).EnsureSuccessStatusCode();
        return client;
    }
}
