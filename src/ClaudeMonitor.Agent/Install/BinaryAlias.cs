using ClaudeMonitor.Agent.Config;

namespace ClaudeMonitor.Agent.Install;

/// <summary>
/// Puts <c>aipod</c> (<c>aipod.exe</c>) beside the installed binary so the command people type is the new name while the file everything else points
/// at (the plugin's hooks, the services, the updater) keeps its old name: a link where the system allows one, else a copy. Best effort: a hook or the
/// daemon must never fail over this, and an alias that is already right is left alone.
/// </summary>
public static class BinaryAlias
{
    /// <summary>Makes or refreshes the alias. Returns its path, or null when it could not be made (nothing is thrown).</summary>
    public static string? Ensure(string binaryPath)
    {
        try
        {
            if (!File.Exists(binaryPath)) return null;
            var dir = Path.GetDirectoryName(binaryPath)!;
            var alias = Path.Combine(dir, Brand.Name + Path.GetExtension(binaryPath));
            if (string.Equals(Path.GetFullPath(alias), Path.GetFullPath(binaryPath), StringComparison.OrdinalIgnoreCase)) return null;
            if (!OperatingSystem.IsWindows())
            {
                var link = new FileInfo(alias);
                if (link.LinkTarget == Path.GetFileName(binaryPath)) return alias;
                if (link.Exists || link.LinkTarget is not null) link.Delete();
                File.CreateSymbolicLink(alias, Path.GetFileName(binaryPath)); // relative: the folder can be moved
                return alias;
            }

            if (File.Exists(alias) && new FileInfo(alias).Length == new FileInfo(binaryPath).Length
                && File.GetLastWriteTimeUtc(alias) >= File.GetLastWriteTimeUtc(binaryPath)) return alias;
            File.Copy(binaryPath, alias, overwrite: true);
            return alias;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or PlatformNotSupportedException)
        {
            return null; // a read-only folder, a running copy that cannot be replaced: the old name still works
        }
    }
}
