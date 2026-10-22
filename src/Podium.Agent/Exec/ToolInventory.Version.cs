using System.Diagnostics;
using Podium.Agent.Auth;

namespace Podium.Agent.Exec;

public static partial class ToolInventory
{
    /// <summary>
    /// The version of one binary, read only when the ADR-0008 rule allows running it: trusted (ADR-0005 executable check) or owned by the
    /// agent's own account; null otherwise, and the file is never started. The seams replace the real checks in tests.
    /// </summary>
    public static string? VersionIfRunnable(string name, string real, TimeSpan timeout, string os, Func<string, bool>? trusted = null,
        Func<string, bool>? selfOwned = null, Func<string, string, TimeSpan, string?>? read = null)
    {
        ArgumentNullException.ThrowIfNull(real);
        var isTrusted = (trusted ?? (r => ExecPaths.IsTrustedExecutable(r, os)))(real);
        if (!isTrusted && !(selfOwned ?? SelfOwned)(real)) return null;
        return (read ?? ReadVersion)(name, real, timeout);
    }

    /// <summary>The first dotted number of a --version line ("Docker version 29.8.2, build 7fc2dff" → "29.8.2"), else its first word, else null.</summary>
    public static string? ShortVersion(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var m = VersionNumber().Match(line);
        return m.Success ? m.Value : line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\d+(\.\d+)+", System.Text.RegularExpressions.RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial System.Text.RegularExpressions.Regex VersionNumber();

    /// <summary>
    /// What changes when a binary changes: the mtime of every existing candidate path (and of the extra paths given), joined. The monitor
    /// re-probes the inventory when this differs from the last probe's stamp, so an upgraded tool shows its new version at the next pass.
    /// </summary>
    public static string Stamp(IEnumerable<ToolCandidate> candidates, params string?[] extraPaths)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var parts = new List<string>();
        foreach (var path in candidates.Select(c => c.Path).Concat(extraPaths.OfType<string>()).Distinct(StringComparer.Ordinal))
        {
            if (path.Contains('*', StringComparison.Ordinal) || !File.Exists(path)) continue;
            try
            {
                parts.Add(path + "@" + File.GetLastWriteTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                parts.Add(path + "@?");
            }
        }

        return string.Join('|', parts);
    }

    /// <summary>The first line of --version, run with an empty environment (a fixed PATH, no HOME), stdin closed, killed at the timeout.</summary>
    private static string? ReadVersion(string name, string real, TimeSpan timeout)
    {
        try
        {
            var info = new ProcessStartInfo(real) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
            foreach (var a in VersionArgs(name)) info.ArgumentList.Add(a);
            info.Environment.Clear();
            if (OperatingSystem.IsWindows())
            {
                info.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                info.Environment["PATH"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
            }
            else
            {
                // The binary's own folder too: an npm or claude shim starts with "#!/usr/bin/env node", and node sits beside it.
                info.Environment["PATH"] = FixedPath + (Path.GetDirectoryName(real) is { Length: > 0 } own ? ":" + own : "");
                info.Environment["LANG"] = "C.UTF-8";
            }

            using var p = Process.Start(info);
            if (p is null) return null;
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                p.Kill(entireProcessTree: true);
                return null;
            }

            if (p.ExitCode != 0) return null;
            var text = output.GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(text)) text = error.GetAwaiter().GetResult(); // java prints its version to stderr
            return FirstLine(text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The first non-empty line, control characters out, at most 100 characters.</summary>
    public static string? FirstLine(string? text)
    {
        var line = (text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null) return null;
        var clean = new string(line.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length > 100 ? clean[..100] : clean;
    }
}
