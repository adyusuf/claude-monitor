using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.StatusLine;

/// <summary>
/// <c>aipod statusline</c>: the status line command Claude Code runs (ADR-0011 §7a, limits). It reads the JSON Claude Code gives on stdin, keeps the usage limits in it
/// (<see cref="LimitsSnapshot"/>) and prints the status line: the one the person had before, if <c>aipod config claude-statusline on</c> wrapped it, else a short text of the limits. It never blocks
/// Claude Code and never fails it: every error goes to stderr and the exit code is 0, as for the hooks.
/// </summary>
public static class StatusLineCommand
{
    public const int InputMax = 1024 * 1024;
    public const int OutputMax = 8 * 1024;
    private static readonly TimeSpan WrappedWait = TimeSpan.FromSeconds(4);

    public static async Task<int> RunAsync(AgentConfig config, TextReader stdin, TextWriter stdout, TextWriter stderr, TimeProvider clock)
    {
        try
        {
            var input = await ReadAsync(stdin);
            LimitsSnapshot? limits = null;
            try
            {
                using var doc = JsonDocument.Parse(input);
                limits = LimitsSnapshot.From(doc.RootElement, clock.GetUtcNow());
            }
            catch (JsonException)
            {
                // not a status line payload: nothing to keep
            }

            if (limits is not null) limits.Save(config);
            var wrapped = Identity.Peek(config)?.StatusLineBackup is { } backup ? OriginalCommand(backup) : null;
            var shown = wrapped is not null ? await RunWrappedAsync(wrapped, input) : null;
            await stdout.WriteAsync(shown ?? (limits ?? LimitsSnapshot.Load(config))?.Text() ?? "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            await stderr.WriteLineAsync($"aipod statusline: {e.GetType().Name}");
        }

        return 0;
    }

    private static async Task<string> ReadAsync(TextReader stdin)
    {
        var buffer = new char[8192];
        var text = new StringBuilder();
        int read;
        while (text.Length < InputMax && (read = await stdin.ReadAsync(buffer)) > 0) text.Append(buffer, 0, read);
        return text.ToString();
    }

    /// <summary>The <c>command</c> of the status line the person had, when it was one.</summary>
    public static string? OriginalCommand(string backupJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(backupJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "command"
                && doc.RootElement.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String ? command.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Runs the person's own status line command with the same input and returns what it printed (cut at <see cref="OutputMax"/>); null when it fails or takes too long.</summary>
    private static async Task<string?> RunWrappedAsync(string command, string input)
    {
        var info = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe") : new ProcessStartInfo("/bin/sh");
        info.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        info.ArgumentList.Add(command);
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
            using var limit = new CancellationTokenSource(WrappedWait);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            var text = await output;
            return process.ExitCode == 0 ? (text.Length > OutputMax ? text[..OutputMax] : text) : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
