using System.Collections.Concurrent;
using Podium.Api.Data;
using Podium.Api.Integrations;
using Podium.Api.Mail;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>
/// The API against a real PostgreSQL 18 in a throw-away container (never a real database: project rule). One
/// container for the whole run; every test creates its own users and workspaces, so tests do not see each other.
/// Mail is captured, time is a clock the test moves, and the external sign-in cookie is replaced by a test handler.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Origin = "http://podium.test.invalid";
    private readonly PostgreSqlContainer db = new PostgreSqlBuilder("postgres:18").Build();

    public ApiFactory()
    {
        Jira = new FakeJira(() => Clock.GetUtcNow());
    }

    public FakeMailer Mail { get; } = new();
    /// <summary>A scripted Atlassian behind the API's "atlassian" HTTP client (ADR-0015): no test reaches a real account.</summary>
    public FakeJira Jira { get; }
    // Starts at the real time: cookies carry expiry dates and the test client's cookie jar judges them by the wall clock.
    public TestClock Clock { get; } = new(DateTimeOffset.UtcNow);
    public string WebRoot { get; } = Path.Combine(Path.GetTempPath(), "ap-web-" + Guid.NewGuid().ToString("N"));
    public string ArchiveDir { get; } = Path.Combine(Path.GetTempPath(), "ap-archive-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        await db.StartAsync();
        Environment.SetEnvironmentVariable("AIPOD_DB", db.GetConnectionString());
        Environment.SetEnvironmentVariable("AIPOD_PUBLIC_ORIGIN", Origin);
        Environment.SetEnvironmentVariable("AIPOD_BACKGROUND_JOBS", "off");
        Environment.SetEnvironmentVariable("AIPOD_ARCHIVE_DIR", ArchiveDir);
        Environment.SetEnvironmentVariable("AIPOD_COMMIT", "test-sha");
        Directory.CreateDirectory(Path.Combine(WebRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(WebRoot, "index.html"), "<!doctype html><title>app</title>");
        await File.WriteAllTextAsync(Path.Combine(WebRoot, "assets", "app.js"), "console.log('app');");
        Environment.SetEnvironmentVariable("AIPOD_WEB_ROOT", WebRoot);
        Environment.SetEnvironmentVariable("AIPOD_AUTH_RATE_PER_MINUTE", "100000");
        Environment.SetEnvironmentVariable("AIPOD_MIN_AGENT_VERSION", "0.2.0");
        Environment.SetEnvironmentVariable("AIPOD_GITHUB_CLIENT_ID", "gh-id");
        Environment.SetEnvironmentVariable("AIPOD_GITHUB_CLIENT_SECRET", "gh-secret");
        Environment.SetEnvironmentVariable("AIPOD_GOOGLE_CLIENT_ID", "g-id");
        Environment.SetEnvironmentVariable("AIPOD_GOOGLE_CLIENT_SECRET", "g-secret");
        Environment.SetEnvironmentVariable("AIPOD_JIRA_CLIENT_ID", FakeJira.ClientId);
        Environment.SetEnvironmentVariable("AIPOD_JIRA_CLIENT_SECRET", FakeJira.ClientSecret);
        await using var scope = Services.CreateAsyncScope();
        var monitorDb = scope.ServiceProvider.GetRequiredService<PodiumDb>();
        await monitorDb.Database.MigrateAsync();
        // As at a real start: the housekeeper creates the monthly partitions before any event arrives.
        await Background.Housekeeper.EnsurePartitionsAsync(monitorDb, Clock.GetUtcNow(), CancellationToken.None);
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await db.DisposeAsync();
        if (Directory.Exists(ArchiveDir)) Directory.Delete(ArchiveDir, recursive: true);
        if (Directory.Exists(WebRoot)) Directory.Delete(WebRoot, recursive: true);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(l => l.AddFilter(level => level >= Microsoft.Extensions.Logging.LogLevel.Error).AddConsole());
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IMailer>(Mail);
            services.AddSingleton<TimeProvider>(Clock);
            services.AddHttpClient(JiraApi.ClientName).ConfigurePrimaryHttpMessageHandler(Jira.Handler);
            services.PostConfigure<AuthenticationOptions>(o =>
            {
                if (o.SchemeMap.TryGetValue(Security.Schemes.External, out var external))
                {
                    external.HandlerType = typeof(TestExternalHandler);
                }
            });
            services.PostConfigureAll<Microsoft.AspNetCore.Authentication.OAuth.OAuthOptions>(o =>
            {
                // The back channel is built before this runs, so it is replaced, not its handler: no test may reach a provider.
                o.Backchannel = new HttpClient(ProviderStub.Handler, disposeHandler: false);
                // The test client's CookieContainer refuses a Set-Cookie whose Path is not under the request path
                // (browsers accept it); the correlation cookie is scoped to the callback path, so widen it here only.
                o.CorrelationCookie.Path = "/";
            });
        });
    }

    public PodiumDb Db() => Services.CreateScope().ServiceProvider.GetRequiredService<PodiumDb>();

    public TestUser NewClient() => new(this, CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri("https://localhost"),
    }));
}

[CollectionDefinition(Name)]
public sealed class ApiGroup : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

public sealed class FakeMailer : IMailer
{
    public ConcurrentQueue<MailMessageData> Sent { get; } = new();

    public Task SendAsync(MailMessageData message, CancellationToken ct)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <summary>The token in the newest mail to this address ("...?token=XYZ").</summary>
    public string TokenFor(string to)
    {
        var mail = Sent.Reverse().First(m => string.Equals(m.To, to, StringComparison.OrdinalIgnoreCase));
        var at = mail.Body.IndexOf("token=", StringComparison.Ordinal) + "token=".Length;
        var end = mail.Body.IndexOfAny(['\n', ' '], at);
        return Uri.UnescapeDataString(mail.Body[at..(end < 0 ? mail.Body.Length : end)]);
    }
}

public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}
