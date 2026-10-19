using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Update;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Config;

/// <summary>
/// "aipod migrate-home" (ADR-0013, section 3.2): moves the old home folder to the aipod folder, on request and never by itself. The folder is renamed in
/// one step (one volume, so the database and its WAL move together) and a symbolic link is left at the old path for old binaries and old plugin wrappers.
/// It refuses rather than guesses: a home named by a variable, a boot service's home, a daemon that will not stop, an aipod folder that already has
/// content, and Windows (a junction and held files need a test machine; the resolver already covers Windows without a move).
/// </summary>
public static class HomeMove
{
    public const string Verb = "migrate-home";
    public const string DryRunOption = "--dry-run";

    /// <summary>The paths and facts the move depends on, apart from the daemon: everything a test needs to pass in.</summary>
    public sealed record Setup(string Os, string UserProfile, bool HomeFromVariable);

    public static async Task<int> RunAsync(string[] args, AgentConfig config, Setup setup, IDaemonControl daemon, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(daemon);
        var dryRun = Array.IndexOf(args, DryRunOption) >= 0;
        var from = HomeResolver.OldHomeFor(setup.Os, setup.UserProfile);
        var to = HomeResolver.NewHomeFor(setup.Os, setup.UserProfile);

        if (Refusal(config, setup, from, to) is { } refusal)
        {
            // "Nothing to do" is not a failure: the machine is already where it should be.
            var ok = refusal.StartsWith("Already", StringComparison.Ordinal);
            await (ok ? stdout : stderr).WriteLineAsync(refusal);
            return ok ? 0 : 2;
        }

        if (dryRun)
        {
            await stdout.WriteLineAsync($"Would move {from} to {to} and leave a link at {from}. Nothing was changed.");
            return 0;
        }

        var wasRunning = daemon.IsRunning();
        if (wasRunning && !await daemon.StopAsync(config.UpdateStopWait, CancellationToken.None) && !daemon.Kill())
        {
            await stderr.WriteLineAsync("The daemon did not stop, so nothing was moved. Try again in a moment.");
            return 1;
        }

        // The lock keeps a daemon from starting between the stop and the rename: the same lock the daemon takes.
        string? failure = null;
        using (var held = DaemonHost.TryLock(config.LockPath))
        {
            if (held is null) failure = "another daemon took over while the move was starting";
            else if (!MoveAndLink(from, to, out var error)) failure = error;
        }

        if (failure is not null)
        {
            await stderr.WriteLineAsync($"Nothing was moved: {failure}.");
            if (wasRunning && !daemon.Start(config.BinaryPath)) await stderr.WriteLineAsync("The daemon could not be started again: run `aipod restart`.");
            return 1;
        }

        await stdout.WriteLineAsync($"Moved {from} to {to}; {from} is now a link to it.");
        if (wasRunning) await stdout.WriteLineAsync(daemon.Start(config.BinaryPath) ? "The daemon was started again." : "The daemon could not be started again: run `aipod restart`.");
        await stdout.WriteLineAsync($"To undo: stop the daemon, then `rm \"{from}\"` and `mv \"{to}\" \"{from}\"`.");
        return 0;
    }

    /// <summary>Why the move cannot or need not happen, or null when it can.</summary>
    private static string? Refusal(AgentConfig config, Setup setup, string from, string to)
    {
        if (setup.Os == OsKinds.Windows) return "Not on Windows yet: it needs a junction and a test machine. The agent finds the old folder by itself, so nothing is lost by waiting.";
        if (setup.HomeFromVariable) return "The home is named by AIPOD_AGENT_HOME or CM_AGENT_HOME; that folder is yours to move.";
        if (config.ServiceMode) return "A boot service's home is set by the service definition (ADR-0013, section 4), not moved here.";
        if (Same(config.Home, to)) return "Already using the aipod folder: " + to;
        if (!Same(config.Home, from)) return $"The agent's home is {config.Home}, which is neither the old folder nor the aipod folder; nothing was moved.";
        if (new DirectoryInfo(from).LinkTarget is not null) return $"Already moved: {from} is a link to the aipod folder.";
        if (!Directory.Exists(from)) return $"There is no folder at {from}: nothing to move.";
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any()) return $"{to} already has content; nothing was moved. Look at both folders and move one by hand.";
        if (File.Exists(to)) return $"{to} is a file; nothing was moved.";
        return Directory.Exists(Path.GetDirectoryName(to)) ? null : $"The folder that should hold {to} does not exist; nothing was moved.";
    }

    /// <summary>Renames the folder, then links the old path to it; if the link fails the rename is undone, so there is never a half-moved home.</summary>
    private static bool MoveAndLink(string from, string to, out string error)
    {
        error = "";
        try
        {
            if (Directory.Exists(to)) Directory.Delete(to); // empty (checked above): a folder an installer left behind
            Directory.Move(from, to);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            return false;
        }

        try
        {
            Directory.CreateSymbolicLink(from, to);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            try
            {
                Directory.Move(to, from);
                error = $"the link could not be made ({e.Message}), so the folder was put back";
            }
            catch (Exception back) when (back is IOException or UnauthorizedAccessException)
            {
                error = $"the link could not be made ({e.Message}) and the folder could not be put back ({back.Message}): it is at {to}; move it back by hand";
            }

            return false;
        }
    }

    private static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.Ordinal);
}
