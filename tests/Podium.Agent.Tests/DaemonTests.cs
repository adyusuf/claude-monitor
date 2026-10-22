using System.Net;
using System.Text.Json;
using Podium.Agent.Auth;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

public sealed class DaemonTests
{
    [Fact]
    public async Task A_daemon_without_a_connection_says_so_and_a_second_daemon_steps_aside()
    {
        using var home = new TempHome();
        var log = new AgentLog(home.Config, TimeProvider.System);
        Assert.Equal(1, await new DaemonHost(home.Config, TimeProvider.System, log).RunAsync(CancellationToken.None));
        Assert.Contains("not connected", await File.ReadAllTextAsync(home.Config.LogPath), StringComparison.Ordinal);

        using var held = DaemonHost.TryLock(home.Config.LockPath);
        Assert.NotNull(held);
        Assert.Null(DaemonHost.TryLock(home.Config.LockPath));
        Assert.Equal(0, await new DaemonHost(home.Config, TimeProvider.System, log).RunAsync(CancellationToken.None));
        Assert.True(DaemonHost.EnsureRunning(home.Config, log)); // already running: nothing is started
    }

    [Fact]
    public void Backoff_doubles_up_to_the_ceiling()
    {
        using var home = new TempHome(c => c with { RetryMax = TimeSpan.FromSeconds(30) });
        var host = new DaemonHost(home.Config, TimeProvider.System, new AgentLog(home.Config, TimeProvider.System));
        Assert.Equal(TimeSpan.FromSeconds(2), host.Backoff(TimeSpan.FromSeconds(2), 0));
        Assert.Equal(TimeSpan.FromSeconds(8), host.Backoff(TimeSpan.FromSeconds(2), 2));
        Assert.Equal(TimeSpan.FromSeconds(30), host.Backoff(TimeSpan.FromSeconds(2), 40));
    }

    [Fact]
    public async Task A_connected_daemon_uploads_beats_and_stops_when_the_web_revokes_it()
    {
        using var home = new TempHome(c => c with { FlushEvery = TimeSpan.FromMilliseconds(20), HeartbeatEvery = TimeSpan.FromMilliseconds(50) });
        var fake = new FakeApi()
            .On("POST /api/agent/batches", body => (HttpStatusCode.OK,
                JsonSerializer.Serialize(new BatchAck(JsonSerializer.Deserialize<EventBatch>(body, Net.ApiClient.Json)!.BatchSeq, false, 1), Net.ApiClient.Json)))
            .On("POST /api/agent/heartbeat", HttpStatusCode.NoContent, "")
            .On("GET /api/agent/settings", HttpStatusCode.OK, new AgentSettings(true, 1000, Guid.NewGuid()))
            .On("GET /api/agent/stream", _ =>
            {
                Thread.Sleep(300);
                return (HttpStatusCode.OK, "event: revoked\ndata: {}\n\n");
            });
        (Identity.Load(home.Config) with { Server = "https://m.invalid", AgentId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid() }).Save(home.Config);
        Credentials.For(home.Config).Write(Credentials.Access, "a");
        using (var store = new LocalStore(home.Config.DatabasePath))
        {
            var payload = JsonSerializer.SerializeToElement(new { x = 1 });
            store.Enqueue(new CapturedEvent(HarnessKinds.ClaudeCode, "s", "hook:Stop", DateTimeOffset.UtcNow, payload), payload.GetRawText());
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var host = new DaemonHost(home.Config, TimeProvider.System, new AgentLog(home.Config, TimeProvider.System), fake);
        Assert.Equal(0, await host.RunAsync(timeout.Token));
        Assert.False(timeout.IsCancellationRequested);
        Assert.True(fake.Count("POST /api/agent/batches") >= 1);
        Assert.True(fake.Count("POST /api/agent/heartbeat") >= 1);
        Assert.Contains("revoked from the web", await File.ReadAllTextAsync(home.Config.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stream_connect_timeout_is_retried_and_does_not_end_the_stream()
    {
        using var home = new TempHome(c => c with { FlushEvery = TimeSpan.FromMilliseconds(20), RetryMax = TimeSpan.FromMilliseconds(100) });
        var streams = 0;
        var fake = new FakeApi()
            .On("POST /api/agent/heartbeat", HttpStatusCode.NoContent, "")
            .On("GET /api/agent/settings", HttpStatusCode.OK, new AgentSettings(true, 1000, Guid.NewGuid()))
            .On("GET /api/agent/stream", _ => Interlocked.Increment(ref streams) == 1
                ? throw new TaskCanceledException("connect timeout", new TimeoutException()) // what SocketsHttpHandler's ConnectTimeout throws
                : (HttpStatusCode.OK, "event: revoked\ndata: {}\n\n"));
        (Identity.Load(home.Config) with { Server = "https://m.invalid", AgentId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid() }).Save(home.Config);
        Credentials.For(home.Config).Write(Credentials.Access, "a");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var host = new DaemonHost(home.Config, TimeProvider.System, new AgentLog(home.Config, TimeProvider.System), fake);
        Assert.Equal(0, await host.RunAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(timeout.IsCancellationRequested);
        Assert.Equal(2, streams);
        var log = await File.ReadAllTextAsync(home.Config.LogPath);
        Assert.Contains("stream failed (1): TaskCanceledException", log, StringComparison.Ordinal);
        Assert.Contains("revoked from the web", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_token_disconnects_the_daemon_and_clears_the_identity()
    {
        using var home = new TempHome(c => c with { FlushEvery = TimeSpan.FromMilliseconds(20) });
        var fake = new FakeApi()
            .On("POST /api/agent/heartbeat", HttpStatusCode.Unauthorized, "{}")
            .On("POST /api/agent/token/refresh", HttpStatusCode.Unauthorized, "{}")
            .On("GET /api/agent/stream", _ =>
            {
                Thread.Sleep(200);
                return (HttpStatusCode.Unauthorized, "{}");
            });
        (Identity.Load(home.Config) with { Server = "https://m.invalid", AgentId = Guid.NewGuid() }).Save(home.Config);
        Credentials.For(home.Config).Write(Credentials.Access, "a");
        Credentials.For(home.Config).Write(Credentials.Refresh, "r");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Assert.Equal(0, await new DaemonHost(home.Config, TimeProvider.System, new AgentLog(home.Config, TimeProvider.System), fake).RunAsync(timeout.Token));
        Assert.False(Identity.Load(home.Config).Connected);
        Assert.Contains("the server gave no reason", await File.ReadAllTextAsync(home.Config.LogPath), StringComparison.Ordinal); // an older server: only the status code is known
    }

    [Theory]
    [InlineData("token_expired", "credentials expired")]
    [InlineData("token_revoked", "it was revoked")]
    [InlineData("agent_revoked", "it was revoked")]
    [InlineData("token_reused", "used twice")]
    [InlineData("unknown_token", "does not know these credentials")]
    public async Task A_refused_refresh_names_the_reason_the_api_gave_and_the_next_step_in_the_log_and_in_status(string reason, string phrase)
    {
        using var home = new TempHome(c => c with { FlushEvery = TimeSpan.FromMilliseconds(20) });
        var fake = new FakeApi()
            .On("POST /api/agent/heartbeat", HttpStatusCode.Unauthorized, "{}")
            .On("POST /api/agent/token/refresh", HttpStatusCode.Unauthorized, $$"""{"title":"invalid_refresh_token","status":401,"reason":"{{reason}}"}""")
            .On("GET /api/agent/stream", _ =>
            {
                Thread.Sleep(200);
                return (HttpStatusCode.Unauthorized, "{}");
            });
        (Identity.Load(home.Config) with { Server = "https://m.invalid", AgentId = Guid.NewGuid() }).Save(home.Config);
        Credentials.For(home.Config).Write(Credentials.Access, "a");
        Credentials.For(home.Config).Write(Credentials.Refresh, "r");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Assert.Equal(0, await new DaemonHost(home.Config, TimeProvider.System, new AgentLog(home.Config, TimeProvider.System), fake).RunAsync(timeout.Token));

        var log = await File.ReadAllTextAsync(home.Config.LogPath);
        Assert.Contains(phrase, log, StringComparison.Ordinal);
        Assert.Contains($"({reason})", log, StringComparison.Ordinal);
        Assert.Contains("aipod login", log, StringComparison.Ordinal);
        using var store = new Storage.LocalStore(home.Config.DatabasePath);
        Assert.Equal(reason, store.Get(AuthRefusal.ReasonKey));
        var status = string.Join("\n", Push.PushStatus.Describe(home.Config, store, DateTimeOffset.UtcNow, null, daemonRunning: false));
        Assert.Contains("Connection: ", status, StringComparison.Ordinal);
        Assert.Contains(phrase, status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_daemon_with_no_refresh_token_stored_says_so_instead_of_blaming_the_server()
    {
        using var home = new TempHome(c => c with { FlushEvery = TimeSpan.FromMilliseconds(20) });
        var fake = new FakeApi()
            .On("POST /api/agent/heartbeat", HttpStatusCode.Unauthorized, "{}")
            .On("GET /api/agent/stream", _ =>
            {
                Thread.Sleep(200);
                return (HttpStatusCode.Unauthorized, "{}");
            });
        (Identity.Load(home.Config) with { Server = "https://m.invalid", AgentId = Guid.NewGuid() }).Save(home.Config);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Assert.Equal(0, await new DaemonHost(home.Config, TimeProvider.System, new AgentLog(home.Config, TimeProvider.System), fake).RunAsync(timeout.Token));

        Assert.Contains("no refresh token is stored", await File.ReadAllTextAsync(home.Config.LogPath), StringComparison.Ordinal);
        Assert.Equal(0, fake.Count("POST /api/agent/token/refresh")); // nothing to send
    }

    [Fact]
    public void A_refusal_message_never_echoes_an_unknown_reason_and_covers_the_odd_cases()
    {
        Assert.DoesNotContain("evil", AuthRefusal.Message("evil\nreason"), StringComparison.Ordinal);
        Assert.Contains("no refresh token", AuthRefusal.Message(Net.ApiClient.NoRefreshToken), StringComparison.Ordinal);
        Assert.Contains("http_503", AuthRefusal.Message("http_503"), StringComparison.Ordinal);
        Assert.Contains("no reason given", AuthRefusal.Message(null), StringComparison.Ordinal);
    }

    [Fact]
    public void The_configuration_reads_its_variables_and_bounds_them()
    {
        var config = AgentConfig.FromEnvironment(k => k switch
        {
            "AP_AGENT_HOME" => "/tmp/ap-x",
            "AP_SERVER" => "https://m.invalid/",
            "AP_PERMISSION_WAIT" => "9999",
            "AP_STOP_WAIT" => "30",
            _ => null,
        });
        Assert.Equal(("/tmp/ap-x", "https://m.invalid", "keychain"), (config.Home, config.ServerOverride, config.CredentialStore));
        Assert.Equal(TimeSpan.FromSeconds(590), config.PermissionWait);
        Assert.Equal(TimeSpan.FromSeconds(30), config.StopWait);
        var defaults = AgentConfig.FromEnvironment(_ => null);
        Assert.Equal(AgentConfig.DefaultHome(), defaults.Home);
        Assert.EndsWith(OperatingSystem.IsMacOS() ? Path.Combine("Application Support", "aipod") : ".aipod", defaults.Home, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.Zero, defaults.StopWait);
        Assert.Equal(OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unsupported", AgentConfig.Os);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Credentials_go_to_the_keychain_through_the_security_tool_without_arguments_carrying_the_secret()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var home = new TempHome();
        // A stand-in for /usr/bin/security: it records its arguments and stdin and keeps one secret in a file.
        var store = Path.Combine(home.Dir, "fake-keychain");
        var tool = Path.Combine(home.Dir, "security");
        File.WriteAllText(tool, $$"""
            #!/bin/sh
            echo "$@" >> "{{store}}.args"
            case "$1" in
              -i) read line; echo "$line" | sed -E 's/.* -w "(.*)"/\1/' > "{{store}}" ;;
              find-generic-password) [ -f "{{store}}" ] && cat "{{store}}" || exit 44 ;;
              delete-generic-password) rm -f "{{store}}" ;;
            esac
            """);
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var keychain = new MacKeychain(tool, "test-service");
        Assert.Null(keychain.Read("access"));
        keychain.Write("access", "tok-123");
        Assert.Equal("tok-123", keychain.Read("access"));
        Assert.DoesNotContain("tok-123", File.ReadAllText(store + ".args"), StringComparison.Ordinal);
        keychain.Delete("access");
        Assert.Null(keychain.Read("access"));
        Assert.Throws<ArgumentException>(() => keychain.Write("access", "bad\"quote"));
        Assert.IsType<MacKeychain>(Credentials.For(home.Config with { CredentialStore = "keychain" }));
    }
}
