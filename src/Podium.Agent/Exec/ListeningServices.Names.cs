using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Podium.Agent.Exec;

public static partial class ListeningServices
{
    public const string Lsof = "/usr/sbin/lsof";
    public static readonly TimeSpan LsofTimeout = TimeSpan.FromSeconds(5);
    private const int Ipv4 = 2;
    private const int Ipv6 = 23;
    private const int TcpTableOwnerPidListener = 3;

    /// <summary>
    /// The (port, process name) pairs of <c>lsof -nP -iTCP -sTCP:LISTEN -Fpcn</c>: a <c>p</c> line starts a process, <c>c</c> names it, each <c>n</c> line is one
    /// listening address (<c>*:5432</c>, <c>127.0.0.1:8080</c>, <c>[::1]:80</c>). The first process listed for a port wins.
    /// </summary>
    public static Dictionary<int, string> ParseLsof(string? output)
    {
        var names = new Dictionary<int, string>();
        string? name = null;
        foreach (var raw in (output ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 2) continue;
            switch (line[0])
            {
                case 'p':
                    name = null;
                    break;
                case 'c':
                    name = Clean(line[1..]);
                    break;
                case 'n' when name is { Length: > 0 }:
                    var colon = line.LastIndexOf(':');
                    if (colon > 0 && int.TryParse(line[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535)
                    {
                        names.TryAdd(port, name);
                    }

                    break;
                default:
                    break;
            }
        }

        return names;
    }

    private static Dictionary<int, string> MacNames()
    {
        // The system's lsof at its fixed path, and only if it is the trusted, root-owned file it should be; an empty environment, no stdin, bounded time.
        if (!File.Exists(Lsof) || !ExecPaths.IsTrustedExecutable(Lsof, Podium.Contracts.OsKinds.MacOs)) return [];
        try
        {
            var info = new ProcessStartInfo(Lsof) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
            foreach (var a in new[] { "-nP", "-iTCP", "-sTCP:LISTEN", "-Fpcn" }) info.ArgumentList.Add(a);
            info.Environment.Clear();
            info.Environment["PATH"] = "/usr/bin:/bin";
            using var p = Process.Start(info);
            if (p is null) return [];
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)LsofTimeout.TotalMilliseconds))
            {
                p.Kill(entireProcessTree: true);
                return [];
            }

            return ParseLsof(output.GetAwaiter().GetResult());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    /// <summary>The (port, owning process id) pairs of one family's listener table (<c>GetExtendedTcpTable</c>, TCP_TABLE_OWNER_PID_LISTENER) as the IP helper lays it out.</summary>
    public static IEnumerable<(int Port, int Pid)> ParseTcpTable(ReadOnlySpan<byte> table, bool ipv6)
    {
        var rows = new List<(int, int)>();
        if (table.Length < 4) return rows;
        var count = BinaryPrimitives.ReadInt32LittleEndian(table);
        var (size, portAt, pidAt) = ipv6 ? (56, 20, 52) : (24, 8, 20);
        for (var i = 0; i < count && 4 + ((i + 1) * size) <= table.Length; i++)
        {
            var row = table[(4 + (i * size))..];
            var raw = BinaryPrimitives.ReadUInt32LittleEndian(row[portAt..]);
            var port = (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF)); // the port is in network byte order in the low 16 bits
            var pid = BinaryPrimitives.ReadInt32LittleEndian(row[pidAt..]);
            if (port is >= 1 and <= 65535) rows.Add((port, pid));
        }

        return rows;
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<int, string> WindowsNames()
    {
        var names = new Dictionary<int, string>();
        foreach (var (family, v6) in new[] { (Ipv4, false), (Ipv6, true) })
        {
            foreach (var (port, pid) in ParseTcpTable(ReadTcpTable(family), v6))
            {
                if (names.ContainsKey(port) || pid <= 0) continue;
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (Clean(process.ProcessName) is { Length: > 0 } n) names[port] = n;
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    // another account's process, or one that ended: the port stays unnamed
                }
            }
        }

        return names;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ReadTcpTable(int family)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);
        if (size <= 0 || size > 16 * 1024 * 1024) return [];
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0) != 0) return [];
            var bytes = new byte[size];
            Marshal.Copy(buffer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("iphlpapi.dll", EntryPoint = "GetExtendedTcpTable")]
    [SupportedOSPlatform("windows")]
    private static partial uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);
}
