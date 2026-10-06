using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.ClaudeUpdate;

public enum ClaudeKind { NotFound, Npm, Native, Unsupported }

/// <summary>The `claude` the agent found, what kind of install it is, and (for the ones it will not touch) why.</summary>
public sealed record ClaudeInstall(ClaudeKind Kind, string? Path, string Detail)
{
    public bool Updatable => Kind is ClaudeKind.Npm or ClaudeKind.Native;
}

/// <summary>
/// Finds the `claude` on the PATH and says how it was installed, from where it really lives. Only a CLI install that updates
/// itself with `claude update` (npm, or the native installer) is ever touched. The desktop app (an .app bundle, an MSIX/Store
/// package) carries its own Claude Code and is updated by the app; a package manager install is not recognised. Whatever is not
/// positively recognised is left alone.
/// </summary>
public static class ClaudeLocator
{
    public static ClaudeInstall Locate(AgentConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var found = config.ClaudeBinary is { Length: > 0 } explicitPath ? (File.Exists(explicitPath) ? explicitPath : null) : Search(config.PathVariable);
        if (found is null) return new(ClaudeKind.NotFound, null, "no `claude` on the PATH");
        var real = Resolve(found);
        return new(Classify(real), real, Describe(Classify(real), real));
    }

    internal static ClaudeKind Classify(string realPath)
    {
        var p = realPath.Replace('\\', '/').ToLowerInvariant();
        if (p.Contains("/windowsapps/") || p.Contains(".app/contents/") || p.Contains("/caskroom/")) return ClaudeKind.Unsupported;
        if (p.Contains("/node_modules/@anthropic-ai/claude-code/")) return ClaudeKind.Npm;
        if (p.Contains("/.local/share/claude/") || p.Contains("/.claude/local/")) return ClaudeKind.Native;
        // Windows npm: the `claude.cmd` shim sits beside node_modules\@anthropic-ai\claude-code
        if (p.EndsWith(".cmd", StringComparison.Ordinal) && Directory.Exists(Path.Combine(Path.GetDirectoryName(realPath) ?? "", "node_modules", "@anthropic-ai", "claude-code"))) return ClaudeKind.Npm;
        return ClaudeKind.Unsupported;
    }

    private static string Describe(ClaudeKind kind, string real) => kind switch
    {
        ClaudeKind.Npm => "an npm install",
        ClaudeKind.Native => "a native install",
        _ => $"an install this agent does not update ({Path.GetFileName(real)}): only npm and native CLI installs are, the desktop app updates itself",
    };

    private static string? Search(string pathVariable)
    {
        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd", "claude" } : ["claude"];
        foreach (var dir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static string Resolve(string path)
    {
        try
        {
            return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Path.GetFullPath(path);
        }
    }
}
