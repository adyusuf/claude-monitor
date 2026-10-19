using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;

namespace Aipod.Tray;

internal static class Program
{
    private const string Usage = "usage: aipod-tray [--status | --install-autostart | --remove-autostart]";

    [STAThread]
    private static int Main(string[] args)
    {
        var config = AgentConfig.FromEnvironment();
        return Run(args, config, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.ProcessPath ?? "aipod-tray",
            Console.Out, Console.Error, () => RunApp(config, args));
    }

    /// <summary>What the command line asks for; <paramref name="runApp"/> is what starts the tray itself (the only part that needs a screen).</summary>
    public static int Run(string[] args, AgentConfig config, string userHome, string self, TextWriter stdout, TextWriter stderr, Func<int> runApp)
    {
        ArgumentNullException.ThrowIfNull(args);
        switch (args.FirstOrDefault())
        {
            case null:
                return runApp();
            case "--status":
                stdout.WriteLine(Describe(new StatusReader(config, TimeProvider.System).Read()));
                return 0;
            case "--install-autostart":
                stdout.WriteLine(Autostart.Enable(new SystemAutostartTarget(), self, userHome));
                return 0;
            case "--remove-autostart":
                stdout.WriteLine(Autostart.Disable(new SystemAutostartTarget(), userHome));
                return 0;
            default:
                stderr.WriteLine(Usage);
                return 2;
        }
    }

    /// <summary>The same text the window shows, for a terminal (and for checking the tray without a screen).</summary>
    public static string Describe(TrayStatus status) =>
        string.Join(Environment.NewLine, [status.Tooltip(), .. status.Lines, Strings.T(Msg.TrayMessages, status.Incoming, status.Outgoing)]);

    private static int RunApp(AgentConfig config, string[] args)
    {
        // One tray per person: a second start finds the lock held and ends quietly.
        Directory.CreateDirectory(config.Home);
        using var instance = DaemonHost.TryLock(Path.Combine(config.Home, "tray.lock"));
        if (instance is null) return 0;
        var controller = new TrayController(new StatusReader(config, TimeProvider.System),
            new TrayActions(config, new ProcessCli(config)), new LedBlinker(TimeProvider.System));
        IClassicDesktopStyleApplicationLifetime? lifetime = null;
        return AppBuilder.Configure(() => new App(controller, () => lifetime?.Shutdown()))
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .StartWithClassicDesktopLifetime(args, l => lifetime = l);
    }
}
