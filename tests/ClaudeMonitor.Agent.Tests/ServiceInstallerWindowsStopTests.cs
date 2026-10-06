using System.Runtime.Versioning;
using ClaudeMonitor.Agent.Install;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>
/// Reinstalling over a running Windows service: "sc stop" returns at once, so the installer waits until "sc query" stops
/// reporting the service as running or stopping before it deletes the old one. Every tool and every query is faked; the
/// Windows layout's drive paths become entries in the working folder on a Unix host, which are removed again.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class ServiceInstallerWindowsStopTests : IDisposable
{
    private const string Running = "STATE : 4  RUNNING";
    private const string StopPending = "STATE : 3  STOP_PENDING";
    private const string Stopped = "STATE : 1  STOPPED";
    private const string QueriedPrefix = "queried -> ";

    private readonly ServiceInstallerFixture fx = new(OsKinds.Windows);
    private readonly string root = "cm-svc-" + Guid.NewGuid().ToString("N")[..12];

    public ServiceInstallerWindowsStopTests() =>
        fx.Layout = fx.Layout with
        {
            BinaryDir = $@"C:\{root}\bin",
            Binary = $@"C:\{root}\bin\cm-agent.exe",
            Home = $@"C:\{root}\home",
            Account = ServiceLayout.WindowsAccount,
        };

    public void Dispose()
    {
        fx.Dispose();
        var here = Directory.GetCurrentDirectory();
        foreach (var entry in Directory.EnumerateFileSystemEntries(here).Where(e => Path.GetFileName(e).StartsWith($@"C:\{root}", StringComparison.Ordinal)).ToList())
        {
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
    }

    /// <summary>Answers "sc query" from the states in order, the last one for ever, and records each answer among the tool calls.</summary>
    private ServiceInstaller Installer(params string[] states)
    {
        var asked = 0;
        string? Read(string tool, IReadOnlyList<string> args)
        {
            var state = states[Math.Min(asked++, states.Length - 1)];
            fx.Calls.Enqueue(QueriedPrefix + state);
            return state;
        }

        return new ServiceInstaller(fx.Output, fx.Run, OsKinds.Windows, isAdmin: true, fx.Layout, fx.Definition, fx.RunDir, Read);
    }

    private static string Command(IReadOnlyList<string> args) => string.Join(' ', args);

    private static int IndexOf(List<string> calls, string tail) => calls.FindIndex(c => c.EndsWith(tail, StringComparison.Ordinal));

    [Fact]
    public void A_reinstall_waits_for_the_old_service_to_stop_and_deletes_it_only_then()
    {
        var code = Installer(Running, StopPending, Stopped).Install(fx.Current, ExecLevels.Argv, allowRoot: false);

        Assert.Equal(0, code);
        var calls = fx.Calls.ToList();
        var stop = IndexOf(calls, Command(WindowsServiceSetup.Stop()));
        var delete = IndexOf(calls, Command(WindowsServiceSetup.Delete()));
        var create = IndexOf(calls, Command(WindowsServiceSetup.Create(fx.Layout)));
        Assert.True(stop >= 0, "the old service is told to stop");
        var polls = calls.Select((c, i) => (c, i)).Where(x => x.c.StartsWith(QueriedPrefix, StringComparison.Ordinal)).ToList();
        Assert.Equal([Running, StopPending, Stopped], polls.Select(x => x.c[QueriedPrefix.Length..]));
        Assert.All(polls, x => Assert.InRange(x.i, stop + 1, delete - 1));
        Assert.InRange(create, delete + 1, int.MaxValue);
        Assert.DoesNotContain("did not stop", fx.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_service_that_is_already_stopped_is_deleted_after_one_look()
    {
        Assert.Equal(0, Installer(Stopped).Install(fx.Current, ExecLevels.Argv, allowRoot: false));

        var calls = fx.Calls.ToList();
        var delete = IndexOf(calls, Command(WindowsServiceSetup.Delete()));
        var polls = calls.Select((c, i) => (c, i)).Where(x => x.c.StartsWith(QueriedPrefix, StringComparison.Ordinal)).ToList();
        Assert.Equal(Stopped, Assert.Single(polls).c[QueriedPrefix.Length..]);
        Assert.True(polls[0].i < delete, "the one look comes before the delete");
    }
}
