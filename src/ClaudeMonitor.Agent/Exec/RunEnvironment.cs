namespace ClaudeMonitor.Agent.Exec;

/// <summary>
/// The environment of a remote run, built from empty (ADR-0004): a fixed PATH, the service home and a UTF-8 locale. Nothing
/// comes from the agent's own environment or from the requester.
/// </summary>
public static class RunEnvironment
{
    public const string UnixPath = "/usr/bin:/bin";
    public const string Locale = "C.UTF-8";
    public const string TempFolder = "tmp";

    /// <summary>Every name a run may have. Anything else is refused by <see cref="AssertClean"/>.</summary>
    public static readonly IReadOnlySet<string> AllowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "LANG", "LC_ALL", "SystemRoot", "USERPROFILE", "TEMP", "TMP",
    };

    /// <summary>Name prefixes that must never reach a run: they load code, change the runtime or redirect tools.</summary>
    public static readonly IReadOnlyList<string> ForbiddenPrefixes = ["CM_", "LD_", "DYLD_", "DOTNET_", "PYTHON", "GIT_"];

    /// <summary>Whole names that must never reach a run (any letter case).</summary>
    public static readonly IReadOnlyList<string> ForbiddenNames =
        ["NODE_OPTIONS", "BASH_ENV", "PAGER", "LESSOPEN", "COMSPEC", "http_proxy", "https_proxy", "all_proxy", "no_proxy"];

    /// <summary>The environment for the current OS. <paramref name="home"/> is the service's home.</summary>
    public static IReadOnlyDictionary<string, string> Build(string home) =>
        Build(home, OperatingSystem.IsWindows(), OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : string.Empty);

    /// <summary>The same for a named OS, so both can be checked on one machine. <paramref name="systemRoot"/> is read on Windows only.</summary>
    public static IReadOnlyDictionary<string, string> Build(string home, bool windows, string systemRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (windows)
        {
            ArgumentException.ThrowIfNullOrEmpty(systemRoot);
            env["PATH"] = $@"{systemRoot}\System32;{systemRoot}";
            env["SystemRoot"] = systemRoot;
            env["USERPROFILE"] = home;
            env["TEMP"] = TempDir(home);
            env["TMP"] = TempDir(home);
        }
        else
        {
            env["PATH"] = UnixPath;
            env["HOME"] = home;
            env["LANG"] = Locale;
            env["LC_ALL"] = Locale;
        }

        AssertClean(env);
        return env;
    }

    /// <summary>The temp folder of a Windows run, inside the home. The caller creates it.</summary>
    public static string TempDir(string home) => Path.Combine(home, TempFolder);

    /// <summary>Throws when an environment holds a forbidden or unknown name. The message names the variable, never its value.</summary>
    public static void AssertClean(IReadOnlyDictionary<string, string> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        foreach (var name in env.Keys)
        {
            if (IsForbidden(name)) throw new InvalidOperationException($"The run environment must not set {name}.");
            if (!AllowedNames.Contains(name)) throw new InvalidOperationException($"The run environment must not set {name}: it is not on the allowed list.");
        }
    }

    public static bool IsForbidden(string name) =>
        ForbiddenPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
        || ForbiddenNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
}
