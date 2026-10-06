using System.Diagnostics;
using System.Net;
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
        if (os == AgentConfig.Unsupported)
        {
            await output.WriteLineAsync("cm-agent runs on macOS, Windows and Linux.");
            return 2;
        }

        var identity = Identity.Load(config);
        var server = (serverArg ?? config.ServerOverride ?? identity.Server)?.TrimEnd('/');
        if (server is null || !Uri.TryCreate(server, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !uri.IsLoopback))
        {
            await output.WriteLineAsync("Give the server: cm-agent login --server https://<your Claude Monitor address> (https, or http on localhost).");
            return 2;
        }

        using var http = ApiClient.CreateHttp(server, handler, config.LoginRequestTimeout);
        using var api = new ApiClient(http, Credentials.For(config), config.ApiCallTimeout);
        DeviceCodeResponse code;
        try
        {
            code = await api.DeviceCodeAsync(new DeviceCodeRequest(identity.MachineKey, Environment.MachineName, os,
                RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), AgentConfig.Version), ct);
        }
        catch (Exception e) when (Transient(e, ct))
        {
            await output.WriteLineAsync($"The server did not answer ({Describe(e)}). Check the address and the connection, then run cm-agent login again.");
            return 1;
        }
        catch (ApiException e) when (e.Status < HttpStatusCode.InternalServerError)
        {
            await output.WriteLineAsync(Refused(e, server));
            return 1;
        }

        await output.WriteLineAsync($"Open {code.VerificationUri} and enter the code {code.UserCode}");
        (openBrowser ?? OpenBrowser)($"{code.VerificationUri}?code={code.UserCode}");

        var interval = TimeSpan.FromSeconds(code.IntervalSeconds);
        var deadline = clock.GetUtcNow().AddSeconds(code.ExpiresInSeconds);
        Exception? failure = null;
        while (clock.GetUtcNow() < deadline)
        {
            await Task.Delay(interval, clock, ct);
            (TokenResponse? Tokens, string? Error) answer;
            try
            {
                answer = await api.DeviceTokenAsync(code.DeviceCode, ct);
            }
            catch (Exception e) when (Transient(e, ct))
            {
                // RFC 8628 §3.5: a connection failure is no answer; keep polling until the code expires, backing off.
                failure = e;
                interval = Backoff(interval);
                await output.WriteLineAsync($"The server did not answer ({Describe(e)}); trying again in {interval.TotalSeconds:0} s.");
                continue;
            }
            catch (ApiException e) when (e.Status < HttpStatusCode.InternalServerError)
            {
                await output.WriteLineAsync(Refused(e, server));
                return 1;
            }

            var unanswered = failure is not null;
            failure = null;
            var (tokens, error) = answer;
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
                    // After a lost answer the code may have been used for tokens that never arrived; it cannot be reused.
                    await output.WriteLineAsync(unanswered
                        ? "The code expired, or it was used for an answer that did not arrive. Run cm-agent login again."
                        : "The code expired. Run cm-agent login again.");
                    return 1;
            }
        }

        await output.WriteLineAsync(failure is null
            ? "The code expired. Run cm-agent login again."
            : $"The code expired while the server did not answer ({Describe(failure)}). Check the connection, then run cm-agent login again.");
        return 1;
    }

    /// <summary>A failure that is no answer: the network, a request that took too long or a gateway error; never the
    /// caller's own cancellation.</summary>
    private static bool Transient(Exception e, CancellationToken ct) =>
        !ct.IsCancellationRequested &&
        e is HttpRequestException or IOException or OperationCanceledException or ApiException { Status: >= HttpStatusCode.InternalServerError };

    private static string Describe(Exception e) =>
        e is ApiException api ? $"it answered {(int)api.Status}" : $"{e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}";

    /// <summary>An answer that polling again cannot change. One line from the status alone: the body may be a proxy's
    /// page and is never printed.</summary>
    private static string Refused(ApiException e, string server) => e.Status switch
    {
        HttpStatusCode.UpgradeRequired =>
            $"The server no longer accepts this cm-agent ({AgentConfig.Version}). Upgrade the agent, then run cm-agent login again.",
        HttpStatusCode.TooManyRequests =>
            "The server is limiting requests from this network. Wait a minute, then run cm-agent login again.",
        _ => $"{server} answered {(int)e.Status}; it does not look like a Claude Monitor server. Check the address, then run cm-agent login again.",
    };

    private TimeSpan Backoff(TimeSpan interval) =>
        interval >= config.LoginPollMax ? interval
        : interval + config.LoginBackoffStep > config.LoginPollMax ? config.LoginPollMax
        : interval + config.LoginBackoffStep;

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
