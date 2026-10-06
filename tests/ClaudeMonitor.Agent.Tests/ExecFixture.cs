using System.Diagnostics;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>A throw-away folder tree for the executor and its guard: a root, an outside folder and an agent home.</summary>
public sealed class ExecFixture : IDisposable
{
    public static readonly bool Unix = !OperatingSystem.IsWindows();
    public static readonly string Os = OperatingSystem.IsMacOS() ? OsKinds.MacOs : OsKinds.Linux;

    /// <summary>A program owned by root in root-owned folders, on both macOS and Linux.</summary>
    public const string TrustedExe = "/usr/bin/true";

    public ExecFixture()
    {
        Dir = Path.Combine(Path.GetTempPath(), "cm-exec-" + Guid.NewGuid().ToString("N"));
        Root = Path.Combine(Dir, "root");
        Outside = Path.Combine(Dir, "outside");
        Home = Path.Combine(Dir, "home");
        foreach (var d in new[] { Root, Outside, Home }) Directory.CreateDirectory(d);
    }

    public string Dir { get; }
    public string Root { get; }
    public string Outside { get; }
    public string Home { get; }

    public void Dispose()
    {
        if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
    }

    public static string File(string folder, string name, string text = "x")
    {
        var path = Path.Combine(folder, name);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    public static RunMessage Argv(string[] argv, GrantTemplate? grant = null, string? cwd = null, Guid? grantId = null, int timeout = 30) =>
        new(Guid.NewGuid(), RunModes.Argv, argv, null, cwd, timeout, DateTimeOffset.UtcNow.AddMinutes(1), grantId ?? (grant is null ? null : Guid.NewGuid()), grant);

    public static RunMessage Shell(string command, string? cwd = null, GrantTemplate? grant = null, Guid? grantId = null, int timeout = 30) =>
        new(Guid.NewGuid(), RunModes.Shell, null, command, cwd, timeout, DateTimeOffset.UtcNow.AddMinutes(1), grantId, grant);

    public static ExecPolicy Level(string level, string[]? executables = null, string[]? roots = null) => new(level, executables ?? [], roots ?? []);

    public ExecDecision Check(RunMessage run, ExecPolicy policy) => ExecGuard.Check(run, policy, Os, Home);

    /// <summary>Runs a program and waits; used for what .NET cannot do itself (a hard link).</summary>
    public static void Tool(params string[] argv)
    {
        var info = new ProcessStartInfo(argv[0]) { UseShellExecute = false };
        foreach (var a in argv.Skip(1)) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}
