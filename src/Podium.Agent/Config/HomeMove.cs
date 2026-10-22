using Podium.Agent.Daemon;
using Podium.Agent.Update;
using Podium.Contracts;

namespace Podium.Agent.Config;

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
            await (refusal.Done ? stdout : stderr).WriteLineAsync(refusal.Text);
            return refusal.Done ? 0 : 2;
        }

        if (dryRun)
        {
            await stdout.WriteLineAsync(Strings.T(Msg.HmWould, from, to));
            return 0;
        }

        var wasRunning = daemon.IsRunning();
        if (wasRunning && !await daemon.StopAsync(config.UpdateStopWait, CancellationToken.None) && !daemon.Kill())
        {
            await stderr.WriteLineAsync(Strings.T(Msg.HmDaemonDidNotStop));
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
            await stderr.WriteLineAsync(Strings.T(Msg.HmNothingMoved, failure));
            if (wasRunning && !daemon.Start(config.BinaryPath)) await stderr.WriteLineAsync(Strings.T(Msg.HmDaemonNotRestarted));
            return 1;
        }

        await stdout.WriteLineAsync(Strings.T(Msg.HmMoved, from, to));
        if (wasRunning) await stdout.WriteLineAsync(Strings.T(daemon.Start(config.BinaryPath) ? Msg.HmDaemonRestarted : Msg.HmDaemonNotRestarted));
        await stdout.WriteLineAsync(Strings.T(Msg.HmUndo, from, to));
        return 0;
    }

    /// <summary>Why the move cannot or need not happen, or null when it can.</summary>
    private static (bool Done, string Text)? Refusal(AgentConfig config, Setup setup, string from, string to)
    {
        if (setup.Os == OsKinds.Windows) return (false, Strings.T(Msg.HmWindows));
        if (setup.HomeFromVariable) return (false, Strings.T(Msg.HmFromVariable));
        if (config.ServiceMode) return (false, Strings.T(Msg.HmServiceMode));
        if (Same(config.Home, to)) return (true, Strings.T(Msg.HmAlreadyAipod, to));
        if (!Same(config.Home, from)) return (false, Strings.T(Msg.HmNeitherFolder, config.Home));
        if (new DirectoryInfo(from).LinkTarget is not null) return (true, Strings.T(Msg.HmAlreadyMoved, from));
        if (!Directory.Exists(from)) return (false, Strings.T(Msg.HmNoFolder, from));
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any()) return (false, Strings.T(Msg.HmTargetHasContent, to));
        if (File.Exists(to)) return (false, Strings.T(Msg.HmTargetIsFile, to));
        return Directory.Exists(Path.GetDirectoryName(to)) ? null : (false, Strings.T(Msg.HmParentMissing, to));
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
