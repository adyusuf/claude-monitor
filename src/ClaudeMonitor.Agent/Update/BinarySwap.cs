namespace ClaudeMonitor.Agent.Update;

/// <summary>How a running binary is put aside. Windows will not overwrite a running exe but will rename it; elsewhere a rename over it is atomic.</summary>
public enum SwapStyle
{
    /// <summary>The previous version is copied to the side, then the new file is renamed over the binary in one step (macOS).</summary>
    Atomic,

    /// <summary>The binary is renamed aside (allowed while it runs), then the new file is renamed into its place (Windows).</summary>
    RenameAside,
}

/// <summary>
/// Replaces the installed binary and keeps exactly one previous version (<c>.prev</c>) for the rollback. Hooks and the MCP
/// entry always run the file at the fixed path, so replacing it in place is enough; processes that already run keep the
/// version they started with until they end (an MCP server lives as long as its Claude session).
/// </summary>
public static class BinarySwap
{
    public static SwapStyle Native => OperatingSystem.IsWindows() ? SwapStyle.RenameAside : SwapStyle.Atomic;

    public static string Previous(string current) => current + ".prev";

    /// <summary>The file the next version is staged as, beside the binary so the final rename never crosses a volume.</summary>
    public static string Staged(string current) => current + ".new";

    public static void Install(string current, string staged, SwapStyle style)
    {
        var previous = Previous(current);
        if (style == SwapStyle.Atomic)
        {
            File.Copy(current, previous, overwrite: true);
            File.Move(staged, current, overwrite: true);
            return;
        }

        RemoveOrSetAside(previous, current);
        File.Move(current, previous);
        try
        {
            File.Move(staged, current);
        }
        catch
        {
            File.Move(previous, current); // the old binary goes back; nothing was changed
            throw;
        }
    }

    /// <summary>Puts the previous version back as the binary; the faulty one is set aside (<c>.bad</c>) so it can be deleted when it no longer runs.</summary>
    public static void Restore(string current, SwapStyle style)
    {
        var previous = Previous(current);
        if (!File.Exists(previous)) throw new FileNotFoundException("there is no previous version to go back to", previous);
        if (style == SwapStyle.Atomic)
        {
            File.Move(previous, current, overwrite: true);
            return;
        }

        var bad = current + ".bad";
        RemoveOrSetAside(bad, current);
        File.Move(current, bad);
        File.Move(previous, current);
    }

    /// <summary>Deletes what an update left that a running process may have kept locked: <c>.old*</c>, <c>.bad</c>, <c>.new</c>. Best effort; the next start tries again.</summary>
    public static void CleanLeftovers(string current)
    {
        var dir = Path.GetDirectoryName(current)!;
        if (!Directory.Exists(dir)) return;
        var name = Path.GetFileName(current);
        foreach (var file in Directory.EnumerateFiles(dir, name + ".*"))
        {
            var suffix = file[(current.Length)..];
            if (suffix != ".new" && suffix != ".bad" && !suffix.StartsWith(".old", StringComparison.Ordinal)) continue;
            TryDelete(file);
        }
    }

    /// <summary>Deletes the file, or, when a running process keeps it locked, renames it to a unique <c>.old</c> name for CleanLeftovers.</summary>
    private static void RemoveOrSetAside(string path, string current)
    {
        if (!File.Exists(path)) return;
        if (TryDelete(path)) return;
        File.Move(path, current + ".old" + Guid.NewGuid().ToString("N")[..8]);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
