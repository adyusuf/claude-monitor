using System.Runtime.InteropServices;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Install;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace ClaudeMonitor.Agent.Daemon;

/// <summary>
/// "cm-agent daemon": started by a hook or the MCP server, or by the OS as a boot service (ADR-0004). It stops on Ctrl+C,
/// SIGTERM (systemd, launchd) or the Windows service manager's stop. A service that is not connected logs in by itself:
/// the device code goes to its log and to login-code.txt in its home, for an admin to read and approve on the web.
/// </summary>
public static class DaemonRole
{
    public const int NotConnected = 1;

    public static async Task<int> RunAsync(AgentConfig config, TimeProvider clock, AgentLog log)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService()) return await AsWindowsServiceAsync(config, clock, log);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        using var term = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            stop.Cancel();
        });
        return await RunUntilAsync(config, clock, log, stop.Token);
    }

    public static async Task<int> RunUntilAsync(AgentConfig config, TimeProvider clock, AgentLog log, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(log);
        if (config.ServiceMode && OperatingSystem.IsWindows() && !DpapiCredentialStore.ProfileLoaded())
        {
            log.Write("the service account's profile is not loaded: refusing to start (its credentials need it)");
            return NotConnected;
        }

        if (config.ServiceMode && !Identity.Load(config).Connected && await ServiceLoginAsync(config, clock, log, stop) != 0) return NotConnected;
        return await new DaemonHost(config, clock, log).RunAsync(stop);
    }

    /// <summary>The device flow for a service: the code is printed to stderr (the service log) and to login-code.txt.</summary>
    private static async Task<int> ServiceLoginAsync(AgentConfig config, TimeProvider clock, AgentLog log, CancellationToken stop)
    {
        if (config.ServerOverride is null)
        {
            log.Write("service not connected and CM_SERVER is not set: install again with --server");
            return NotConnected;
        }

        config.EnsureHome();
        var path = Path.Combine(config.Home, ServiceLayout.LoginCodeFile);
        await using var file = new StreamWriter(path, append: false);
        await using var both = new TeeWriter(Console.Error, file);
        var code = await new Login(config, both, clock, _ => false).RunAsync(config.ServerOverride, null, stop);
        log.Write(code == 0 ? "service logged in" : "service login did not complete");
        return code;
    }

    private static async Task<int> AsWindowsServiceAsync(AgentConfig config, TimeProvider clock, AgentLog log)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceLayout.ServiceName);
        builder.Services.AddHostedService(sp => new ServiceWorker(config, clock, log, sp.GetRequiredService<IHostApplicationLifetime>()));
        await builder.Build().RunAsync();
        return Environment.ExitCode;
    }

    private sealed class ServiceWorker(AgentConfig config, TimeProvider clock, AgentLog log, IHostApplicationLifetime lifetime) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Environment.ExitCode = await RunUntilAsync(config, clock, log, stoppingToken);
            lifetime.StopApplication();
        }
    }

    /// <summary>Writes every line to two writers (the service log and the login-code file); flushes each time.</summary>
    private sealed class TeeWriter(TextWriter first, TextWriter second) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            first.Write(value);
            second.Write(value);
        }

        public override async Task WriteLineAsync(string? value)
        {
            await first.WriteLineAsync(value);
            await second.WriteLineAsync(value);
            await first.FlushAsync();
            await second.FlushAsync();
        }
    }
}
