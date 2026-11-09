using Podium.Agent.Config;
using Podium.Agent.Exec;
using Podium.Contracts;

namespace Podium.Agent.Access;

/// <summary>
/// <c>aipod config access show | allow &lt;folder&gt; | deny &lt;folder&gt; | app &lt;exe&gt; [--sha256 &lt;hex&gt;] | remove &lt;path&gt; | clear</c> (ADR-0020 §4): the machine's
/// admin narrows what remote runs may use here, below whatever a web policy says. Entries must exist and are kept as real paths; an allowed folder is
/// never a system or home root. On a service machine the ceiling is in the admin-owned exec policy file, so only the admin can change it.
/// </summary>
public static partial class AccessCommand
{
    public const string Verb = "access";
    private const string Sha256Option = "--sha256";

    public static async Task<int> RunAsync(string[] args, AgentConfig config, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var verb = args.Length >= 3 ? args[2] : null;
        if (verb == "show" && args.Length == 3) return await ShowAsync(config, stdout, stderr);
        var change = verb switch
        {
            "allow" when args.Length == 4 => Change.Allow,
            "deny" when args.Length == 4 => Change.Deny,
            "app" when args.Length == 4 || (args.Length == 6 && args[4] == Sha256Option) => Change.App,
            "remove" when args.Length == 4 => Change.Remove,
            "clear" when args.Length == 3 => Change.Clear,
            _ => Change.None,
        };
        if (change == Change.None)
        {
            await stderr.WriteLineAsync(Strings.T(Msg.AccessUsage));
            return 2;
        }

        if (AccessCeilingFile.IsService(config) && !ExecPolicyLoader.RunningAsRoot())
        {
            await stderr.WriteLineAsync(Strings.T(Msg.AccessNeedsAdmin));
            return 1;
        }

        if (AccessCeilingFile.Load(config) is not { } loaded)
        {
            await stderr.WriteLineAsync(Strings.T(Msg.AccessNoServiceFile, config.ExecConfigPath));
            return 1;
        }

        var (code, ceiling, message) = Apply(change, args, loaded.Ceiling, AgentConfig.Os);
        if (ceiling is null)
        {
            await stderr.WriteLineAsync(message);
            return code;
        }

        try
        {
            AccessCeilingFile.Save(config, ceiling);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await stderr.WriteLineAsync(Strings.T(Msg.AccessWriteFailed, e.GetType().Name));
            return 1;
        }

        await stdout.WriteLineAsync(message);
        return 0;
    }

    private enum Change
    {
        None,
        Allow,
        Deny,
        App,
        Remove,
        Clear,
    }

    /// <summary>The new ceiling and what to say, or a null ceiling, the exit code and why nothing changed.</summary>
    private static (int Code, AccessCeiling? Ceiling, string Message) Apply(Change change, string[] args, AccessCeiling current, string os)
    {
        switch (change)
        {
            case Change.Allow or Change.Deny:
                var folder = PathIdentity.Resolve(args[3], os, allowMissing: false);
                if (folder is null || !Directory.Exists(folder)) return (2, null, Strings.T(Msg.AccessNotFolder));
                if (change == Change.Allow && GrantMatcher.IsForbiddenRoot(folder, os)) return (2, null, Strings.T(Msg.AccessForbiddenRoot, folder));
                if (change == Change.Allow)
                {
                    var allowed = Add(current.AllowedRoots, folder, os);
                    return (0, current with { AllowedRoots = allowed }, Strings.T(Msg.AccessAllowedDone, folder, allowed.Count) + Strings.T(Msg.AccessNoShell));
                }

                return (0, current with { DeniedRoots = Add(current.DeniedRoots, folder, os) }, Strings.T(Msg.AccessDeniedDone, folder) + Strings.T(Msg.AccessNoShell));
            case Change.App:
                var exe = PathIdentity.Resolve(args[3], os, allowMissing: false);
                if (exe is null || !File.Exists(exe) || !GrantMatcher.IsAbsoluteExecutable(exe, os)) return (2, null, Strings.T(Msg.AccessNotExecutable));
                var sha = args.Length == 6 ? Lower(args[5]) : null;
                if (args.Length == 6 && !AccessPolicyRules.IsSha256(sha)) return (2, null, Strings.T(Msg.AccessBadHash));
                List<AccessApp> apps = [.. current.Executables.Where(a => !Same(a.Path, exe, os)), new AccessApp(exe, sha)];
                return (0, current with { Executables = apps },
                    Strings.T(Msg.AccessAppDone, exe, sha is null ? "" : Strings.T(Msg.AccessAppHashed), apps.Count) + Strings.T(Msg.AccessNoShell));
            case Change.Remove:
                var real = PathIdentity.Resolve(args[3], os, allowMissing: true);
                bool Gone(string p) => Same(p, args[3], os) || (real is not null && Same(p, real, os));
                var next = new AccessCeiling([.. current.AllowedRoots.Where(p => !Gone(p))], [.. current.DeniedRoots.Where(p => !Gone(p))],
                    [.. current.Executables.Where(a => !Gone(a.Path))]);
                return next.AllowedRoots.Count + next.DeniedRoots.Count + next.Executables.Count == current.AllowedRoots.Count + current.DeniedRoots.Count + current.Executables.Count
                    ? (1, null, Strings.T(Msg.AccessNotInCeiling, args[3]))
                    : (0, next, Strings.T(Msg.AccessRemoved, args[3]));
            case Change.Clear:
                return (0, AccessCeiling.Empty, Strings.T(Msg.AccessCleared));
            case Change.None:
            default:
                return (2, null, Strings.T(Msg.AccessUsage));
        }
    }

    private static List<string> Add(IReadOnlyList<string> list, string path, string os) => [.. list.Where(p => !Same(p, path, os)), path];

    private static bool Same(string a, string b, string os) => string.Equals(a, b, os == OsKinds.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static string Lower(string text) => new([.. text.Select(c => char.IsAsciiLetterUpper(c) ? (char)(c + ('a' - 'A')) : c)]);
}
