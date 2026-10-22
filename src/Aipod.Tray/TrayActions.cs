using System.ComponentModel;
using System.Diagnostics;
using Podium.Agent.Auth;
using Podium.Agent.Config;

namespace Aipod.Tray;

/// <summary>What a run of the aipod command line left behind.</summary>
public sealed record CliResult(int ExitCode, string Output);

/// <summary>Runs the aipod command line (the tray never does the agent's work itself) and opens things in the system's own way.</summary>
public interface IAgentCli
{
    Task<CliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct);

    /// <summary>Opens a folder, a file or an http(s) address with the system's default program.</summary>
    void Open(string target);
}

/// <summary>The outcome of a command for the window: whether it worked and what to tell the person.</summary>
public sealed record ActionResult(bool Ok, string Text);

/// <summary>Carries out a <see cref="TrayCommand"/> by running the matching aipod command or opening the right thing.</summary>
public sealed class TrayActions(AgentConfig config, IAgentCli cli)
{
    public async Task<ActionResult> RunAsync(TrayCommand command, string? server, CancellationToken ct)
    {
        switch (command)
        {
            case TrayCommand.Reconnect:
                return await Cli(Strings.T(Msg.TrayActReconnecting), ["restart"], ct);
            case TrayCommand.Disconnect:
                return await Cli(Strings.T(Msg.TrayActDisconnected), ["logout"], ct);
            case TrayCommand.CheckForUpdate:
                return await Cli("", ["update", "--check"], ct);
            case TrayCommand.UpdateNow:
                return await Cli("", ["update"], ct);
            case TrayCommand.Login:
                var address = (string.IsNullOrWhiteSpace(server) ? Identity.Peek(config)?.Server : server)?.Trim();
                if (!IsWebAddress(address)) return new ActionResult(false, Strings.T(Msg.TrayActGiveServer));
                return await Cli("", ["connect", "--server", address!], ct);
            case TrayCommand.OpenWeb:
                return Open(Identity.Peek(config)?.Server, Strings.T(Msg.TrayActNoServer));
            case TrayCommand.OpenLog:
                return File.Exists(config.LogPath) ? Open(config.LogPath, "") : new ActionResult(false, Strings.T(Msg.TrayActNoLog));
            default:
                return new ActionResult(false, Strings.T(Msg.TrayActNotCommand));
        }
    }

    /// <summary>Only http and https addresses are ever opened or logged in to.</summary>
    public static bool IsWebAddress(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.Host.Length > 0;

    private async Task<ActionResult> Cli(string done, IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            var result = await cli.RunAsync(args, ct);
            var text = result.Output.Trim();
            return new ActionResult(result.ExitCode == 0, text.Length > 0 ? text : result.ExitCode == 0 ? done : Strings.T(Msg.TrayActFailed, args[0], result.ExitCode));
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException or TimeoutException)
        {
            return new ActionResult(false, Strings.T(Msg.TrayActCouldNotRun, args[0], e.GetType().Name));
        }
    }

    private ActionResult Open(string? target, string missing)
    {
        if (target is null) return new ActionResult(false, missing);
        if (target.Contains("://", StringComparison.Ordinal) && !IsWebAddress(target)) return new ActionResult(false, Strings.T(Msg.TrayActOnlyHttp));
        try
        {
            cli.Open(target);
            return new ActionResult(true, "");
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            return new ActionResult(false, Strings.T(Msg.TrayActCouldNotOpen, e.GetType().Name));
        }
    }
}

/// <summary>The real thing: starts the installed binary (the aipod name when it is there) and waits for it, bounded.</summary>
public sealed class ProcessCli(AgentConfig config, TimeSpan? timeout = null) : IAgentCli
{
    private readonly TimeSpan wait = timeout ?? TimeSpan.FromSeconds(90);

    public async Task<CliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        var info = new ProcessStartInfo(config.BinaryPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("the process did not start");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(wait);
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var error = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException();
        }

        return new CliResult(process.ExitCode, await output + await error);
    }

    public void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
}
