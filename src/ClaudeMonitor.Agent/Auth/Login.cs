using System.Diagnostics;
using System.Runtime.InteropServices;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Auth;

/// <summary>
/// "cm-agent login --server &lt;url&gt;": connects this machine with the device flow (RFC 8628). The person approves the
/// shown code on the web while signed in; the agent never sees a password.
/// </summary>
public sealed class Login(AgentConfig config, TextWriter output, TimeProvider clock, Func<string, bool>? openBrowser = null,
    string? os = null)
{
    private readonly string os = os ?? AgentConfig.Os;

    public async Task<int> RunAsync(string? serverArg, HttpMessageHandler? handler, CancellationToken ct)
    {
        if (os == "unsupported")
        {
            await output.WriteLineAsync("cm-agent runs on macOS and Windows.");
            return 2;
        }

        var identity = Identity.Load(config);
        var server = (serverArg ?? config.ServerOverride ?? identity.Server)?.TrimEnd('/');
        if (server is null || !Uri.TryCreate(server, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !uri.IsLoopback))
        {
            await output.WriteLineAsync("Give the server: cm-agent login --server https://<your Claude Monitor address> (https, or http on localhost).");
            return 2;
        }

        using var http = ApiClient.CreateHttp(server, handler);
        using var api = new ApiClient(http, Credentials.For(config));
        var code = await api.DeviceCodeAsync(new DeviceCodeRequest(identity.MachineKey, Environment.MachineName, os,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), AgentConfig.Version), ct);
        await output.WriteLineAsync($"Open {code.VerificationUri} and enter the code {code.UserCode}");
        (openBrowser ?? OpenBrowser)($"{code.VerificationUri}?code={code.UserCode}");

        var interval = TimeSpan.FromSeconds(code.IntervalSeconds);
        var deadline = clock.GetUtcNow().AddSeconds(code.ExpiresInSeconds);
        while (clock.GetUtcNow() < deadline)
        {
            await Task.Delay(interval, clock, ct);
            var (tokens, error) = await api.DeviceTokenAsync(code.DeviceCode, ct);
            if (tokens is not null)
            {
                api.SaveTokens(tokens);
                (identity with { Server = server, AgentId = tokens.AgentId, WorkspaceId = tokens.WorkspaceId }).Save(config);
                await output.WriteLineAsync("Connected. This machine now reports to Claude Monitor.");
                return 0;
            }

            switch (error)
            {
                case DeviceTokenErrors.SlowDown:
                    interval += TimeSpan.FromSeconds(5);
                    break;
                case DeviceTokenErrors.Pending:
                    break;
                case DeviceTokenErrors.Denied:
                    await output.WriteLineAsync("The request was denied on the web.");
                    return 1;
                default:
                    await output.WriteLineAsync("The code expired. Run cm-agent login again.");
                    return 1;
            }
        }

        await output.WriteLineAsync("The code expired. Run cm-agent login again.");
        return 1;
    }

    /// <summary>"cm-agent logout": forgets the tokens here; the web shows the machine until it is revoked there.</summary>
    public async Task<int> LogoutAsync()
    {
        var store = Credentials.For(config);
        store.Delete(Credentials.Access);
        store.Delete(Credentials.Refresh);
        var identity = Identity.Load(config);
        (identity with { AgentId = null, WorkspaceId = null }).Save(config);
        await output.WriteLineAsync("Signed out on this machine. Revoke it on the web (Machines) to remove it there too.");
        return 0;
    }

    private static bool OpenBrowser(string url)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return p is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
