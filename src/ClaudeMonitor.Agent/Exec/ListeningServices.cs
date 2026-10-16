using System.Globalization;
using System.Net.NetworkInformation;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>
/// The TCP ports this machine listens on and, where the OS lets the agent see it, the name of the process that owns each (ADR-0008 §3a′).
/// Read only and no new dependency: the ports come from the framework's socket table, the names from <c>/proc</c> on Linux (only for
/// processes the agent's own account may inspect; another account's process shows the port with no name). It starts no process. Sent
/// only when the machine's owner switched the report on.
/// </summary>
public static class ListeningServices
{
    public const int Max = 64;
    private const int ProcessesScanned = 4000;
    private const string Tcp = "tcp";

    public static IReadOnlyList<ServiceInfo> Live() =>
        Probe(Ports, OperatingSystem.IsLinux() ? LinuxNames : () => new Dictionary<int, string>());

    /// <summary>The services of a port list and a (port to process name) map; one entry per port, lowest ports first, at most <see cref="Max"/>.</summary>
    public static IReadOnlyList<ServiceInfo> Probe(Func<IReadOnlyList<int>> ports, Func<IReadOnlyDictionary<int, string>> names)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(names);
        var known = names();
        return [.. ports().Where(p => p is >= 1 and <= 65535).Distinct().Order().Take(Max)
            .Select(p => new ServiceInfo(p, Tcp, known.TryGetValue(p, out var name) && name.Length > 0 ? name : null))];
    }

    private static IReadOnlyList<int> Ports()
    {
        try
        {
            return [.. IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port)];
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    /// <summary>The (inode, port) pairs of the listening sockets in a <c>/proc/net/tcp</c> or <c>tcp6</c> table (state 0A).</summary>
    public static IEnumerable<(long Inode, int Port)> ParseProcNet(string? table)
    {
        foreach (var line in (table ?? "").Split('\n').Skip(1))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 10 || f[3] != "0A") continue;
            var colon = f[1].LastIndexOf(':');
            if (colon < 0 || !int.TryParse(f[1][(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port)) continue;
            if (!long.TryParse(f[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var inode) || inode == 0) continue;
            yield return (inode, port);
        }
    }

    private static Dictionary<int, string> LinuxNames()
    {
        var byInode = new Dictionary<long, int>();
        foreach (var file in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            try
            {
                foreach (var (inode, port) in ParseProcNet(File.ReadAllText(file))) byInode[inode] = port;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a table the agent cannot read gives no names
            }
        }

        var result = new Dictionary<int, string>();
        if (byInode.Count == 0) return result;
        try
        {
            foreach (var pidDir in Directory.EnumerateDirectories("/proc").Where(d => int.TryParse(Path.GetFileName(d), out _)).Take(ProcessesScanned))
            {
                NamesOf(pidDir, byInode, result);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // names stay what was found so far
        }

        return result;
    }

    private static void NamesOf(string pidDir, Dictionary<long, int> byInode, Dictionary<int, string> result)
    {
        try
        {
            foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(pidDir, "fd")))
            {
                var target = new FileInfo(fd).LinkTarget;
                if (target is null || !target.StartsWith("socket:[", StringComparison.Ordinal) || !target.EndsWith(']')) continue;
                if (!long.TryParse(target.AsSpan(8, target.Length - 9), NumberStyles.Integer, CultureInfo.InvariantCulture, out var inode)) continue;
                if (byInode.TryGetValue(inode, out var port) && !result.ContainsKey(port)) result[port] = ProcessName(pidDir);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // another account's process: its sockets stay unnamed
        }
    }

    private static string ProcessName(string pidDir)
    {
        try
        {
            return Clean(File.ReadAllText(Path.Combine(pidDir, "comm")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>A process name as shown: printable characters only, at most 64.</summary>
    public static string Clean(string name)
    {
        var clean = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > 64 ? clean[..64] : clean;
    }
}
