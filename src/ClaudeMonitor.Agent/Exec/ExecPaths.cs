using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>Real paths and the trust of the program file, for <see cref="ExecGuard"/>.</summary>
internal static class ExecPaths
{
    private const int MaxLinks = 40;
    private const char UnixSeparator = '/';
    private const char WindowsSeparator = '\\';

    /// <summary>
    /// The path with every link on the way followed, component by component. Null when it is not absolute, a link
    /// loops or cannot be read, or (unless allowMissing) a component does not exist; with allowMissing the part that
    /// does not exist yet is kept as written, since no link can be in it.
    /// </summary>
    public static string? RealPath(string? path, string os, bool allowMissing)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var windows = os == OsKinds.Windows;
        var separators = windows ? new[] { WindowsSeparator, UnixSeparator } : new[] { UnixSeparator };
        var root = RootOf(path, windows);
        if (root is null)
        {
            return null;
        }

        var pending = new Stack<string>();
        Push(pending, path[root.Length..], separators);
        var current = root;
        var links = 0;
        var missing = false;
        while (pending.Count > 0)
        {
            var segment = pending.Pop();
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            var next = Path.EndsInDirectorySeparator(current) ? current + segment : current + Path.DirectorySeparatorChar + segment;
            if (missing)
            {
                current = next;
                continue;
            }

            string? target;
            try
            {
                // LinkTarget is null both for a plain entry and for one that does not exist: existence is checked first.
                var entry = new FileInfo(next);
                if (!entry.Exists && !Directory.Exists(next) && entry.LinkTarget is null)
                {
                    if (!allowMissing)
                    {
                        return null;
                    }

                    missing = true;
                    current = next;
                    continue;
                }

                target = entry.LinkTarget;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null; // unreadable is unresolved, and unresolved is refused
            }

            if (target is null)
            {
                current = next;
                continue;
            }

            if (++links > MaxLinks)
            {
                return null;
            }

            var targetRoot = RootOf(target, windows);
            if (targetRoot is not null)
            {
                current = targetRoot;
                target = target[targetRoot.Length..];
            }

            Push(pending, target, separators);
        }

        return current;
    }

    private static string? RootOf(string path, bool windows)
    {
        if (!windows)
        {
            return path[0] == UnixSeparator ? UnixSeparator.ToString() : null;
        }

        var root = Path.GetPathRoot(path);
        return root is { Length: >= 3 } && root[1] == ':' ? root : null;
    }

    private static void Push(Stack<string> pending, string path, char[] separators)
    {
        var parts = path.Split(separators);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            pending.Push(parts[i]);
        }
    }

    /// <summary>
    /// A program file the service account cannot have replaced: on Unix it and every folder above it is owned by
    /// root and writable by neither group nor others. Windows relies on the install folder's ACL and the .exe rule;
    /// the Administrators-owned check is v2. Where the owner cannot be read (see <see cref="UnixFileInfo"/>) only the
    /// permission bits are checked.
    /// </summary>
    public static bool IsTrustedExecutable(string realExe, string os)
    {
        if (os == OsKinds.Windows || OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            for (var path = realExe; path is not null; path = Path.GetDirectoryName(path))
            {
                var mode = File.GetUnixFileMode(path);
                if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                {
                    return false;
                }

                var stat = UnixFileInfo.TryGet(path);
                if (stat is { OwnerUid: not 0 })
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A file with more than one hard link can be reached from outside the root it seems to sit in.</summary>
    public static bool HasExtraLinks(string realPath, string os)
    {
        if (os == OsKinds.Windows || !File.Exists(realPath))
        {
            return false;
        }

        return UnixFileInfo.TryGet(realPath) is { LinkCount: > 1 };
    }
}
