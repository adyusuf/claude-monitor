using System.Collections.Concurrent;
using ClaudeMonitor.Agent.Install;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Temp paths for the installer and a fake tool runner: the real service manager, accounts and system folders are never touched.</summary>
public sealed class ServiceInstallerFixture : IDisposable
{
    public ServiceInstallerFixture(string os)
    {
        Os = os;
        // /tmp, not the per-user temp folder: its random name may hold characters a systemd unit refuses.
        Dir = Path.Combine(Path.GetTempPath().StartsWith("/var/folders", StringComparison.Ordinal) ? "/tmp" : Path.GetTempPath(), "cm-svc-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Dir);
        Layout = new ServiceLayout(os, $"{Dir}/bin", $"{Dir}/bin/cm-agent", $"{Dir}/home", os == OsKinds.Linux ? "cm-agent" : "_cmagent", $"{Dir}/etc/exec.json");
        Definition = $"{Dir}/definition";
        RunDir = $"{Dir}/run-systemd";
        Directory.CreateDirectory(RunDir);
        Current = $"{Dir}/current-cm-agent";
        File.WriteAllText(Current, "binary-v2");
    }

    public string Os { get; }
    public string Dir { get; }
    public ServiceLayout Layout { get; set; }
    public string Definition { get; }
    public string RunDir { get; set; }
    public string Current { get; }
    public StringWriter Output { get; } = new();
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>Decides a tool's exit code from "tool arg arg"; the default is success.</summary>
    public Func<string, int> Answer { get; set; } = _ => 0;

    public string? DsclList { get; set; } = "root 0\n_other 250\n";

    public int Run(string tool, IReadOnlyList<string> args)
    {
        var line = $"{tool} {string.Join(' ', args)}";
        Calls.Enqueue(line);
        return Answer(line);
    }

    public ServiceInstaller Installer(bool admin = true) =>
        new(Output, Run, Os, admin, Layout, Definition, RunDir, (_, _) => DsclList);

    public string Text => Output.ToString();

    public bool Called(string startsWith) => Calls.Any(c => c.StartsWith(startsWith, StringComparison.Ordinal));

    public void Dispose()
    {
        if (!Directory.Exists(Dir)) return;
        foreach (var link in Directory.EnumerateFileSystemEntries(Dir, "*", SearchOption.AllDirectories).Where(e => new FileInfo(e).LinkTarget is not null).ToList()) File.Delete(link);
        Directory.Delete(Dir, recursive: true);
    }
}
