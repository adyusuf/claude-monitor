using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.Install;

/// <summary>Starts a program that outlives this one (a seam: tests never start a real login).</summary>
public interface IDetachedStarter
{
    bool Start(string exe, IReadOnlyList<string> args);
}

/// <summary>
/// "aipod connect [--server url]" (ADR-0007): the device login from inside a Claude Code session. A login waits for a person to approve a
/// code on the web, which can take minutes, so this starts it detached (writing to <see cref="AgentConfig.ConnectLogPath"/>), waits only until the code
/// has appeared, prints it and returns. The detached login saves the identity and starts the daemon when the code is approved. A code is shown
/// again only while that login is alive (nothing but retries after it in the log) and was started for the same server.
/// </summary>
public sealed class ConnectCommand(AgentConfig config, TextWriter output, TimeProvider clock, IDetachedStarter starter)
{
    public const string CodeMarker = "enter the code";

    public async Task<int> RunAsync(string? serverArg, string exe, CancellationToken ct)
    {
        var identity = Identity.Load(config);
        if (identity.Connected)
        {
            await output.WriteLineAsync($"Already connected to {identity.Server}. Run aipod logout first to connect to another server.");
            return 0;
        }

        var server = (serverArg ?? config.ServerOverride ?? identity.Server)?.TrimEnd('/');
        if (server is null || !Uri.TryCreate(server, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !uri.IsLoopback))
        {
            await output.WriteLineAsync("Give the server: aipod connect --server https://<your aipod server address> (https, or http on localhost).");
            return 2;
        }

        config.EnsureHome();
        if (ShownCode(server) is { } shown)
        {
            await Show(shown);
            return 0;
        }

        File.WriteAllText(config.ConnectLogPath, "");
        File.WriteAllText(config.ConnectServerPath, server);
        if (!starter.Start(exe, ["login", "--server", server, "--output", config.ConnectLogPath, "--detach"]))
        {
            await output.WriteLineAsync("Could not start the login. Run: aipod login --server " + server);
            return 1;
        }

        var deadline = clock.GetUtcNow() + config.ConnectWait;
        while (clock.GetUtcNow() < deadline)
        {
            if (ShownCode(server) is { } code)
            {
                await Show(code);
                return 0;
            }

            await Task.Delay(config.PollEvery, clock, ct);
        }

        await output.WriteLineAsync($"The login did not show a code yet. Look at {config.ConnectLogPath}, or run aipod login --server {server} in a terminal.");
        return 1;
    }

    /// <summary>
    /// The login line with the code, when it is still good: written recently, for this server, and followed by nothing but retries. Anything else after it
    /// (approved, denied, expired, a failure) means that login is over and its code is dead.
    /// </summary>
    private string? ShownCode(string server)
    {
        try
        {
            if (!File.Exists(config.ConnectLogPath) || clock.GetUtcNow() - File.GetLastWriteTimeUtc(config.ConnectLogPath) > config.ConnectReuse) return null;
            // For this server: the verification page is the API's public origin, which need not be the address typed here, so the server is recorded when the login starts.
            if (!File.Exists(config.ConnectServerPath) || File.ReadAllText(config.ConnectServerPath) != server) return null;
            var lines = File.ReadAllLines(config.ConnectLogPath);
            var at = Array.FindLastIndex(lines, l => l.Contains(CodeMarker, StringComparison.Ordinal));
            if (at < 0) return null;
            return lines.Skip(at + 1).All(l => l.Length == 0 || l.Contains("trying again", StringComparison.Ordinal)) ? lines[at] : null;
        }
        catch (IOException)
        {
            return null; // the login is writing it just now: the next look sees it
        }
    }

    private Task Show(string line) => output.WriteLineAsync(
        line + Environment.NewLine + "Approve it on that page while signed in. This machine then connects by itself; aipod status shows when.");
}
