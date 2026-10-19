using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Aipod.Tray;

/// <summary>The Avalonia application: the tray icon with its tooltip and menu, the blinking lights and the status window.</summary>
public sealed class App(TrayController controller, Action quit) : Application, IDisposable
{
    private readonly TrayIcon icon = new() { IsVisible = true, ToolTipText = "aipod" };
    private StatusWindow? window;
    private Leds shown = new(true, true);
    private string menuKey = "";

    public override void Initialize() => Styles.Add(new FluentTheme());

    public void Dispose() => icon.Dispose();

    internal TrayIcon Icon => icon;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        controller.Refresh();
        Redraw(); // the icon, tooltip and menu are filled in BEFORE the icon is handed to the system: macOS refuses a missing tooltip
        TrayIcon.SetIcons(this, [icon]);
        _ = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Lights());
        _ = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) => await Task.Run(controller.RefreshLights));
        _ = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, async (_, _) =>
        {
            await Task.Run(controller.Refresh);
            Redraw();
        });
        base.OnFrameworkInitializationCompleted();
    }

    internal void Lights()
    {
        var now = controller.Leds;
        if (now == shown) return;
        shown = now;
        using var png = new MemoryStream(LedIcon.Png(now));
        icon.Icon = new WindowIcon(new Bitmap(png));
        window?.Render();
    }

    /// <summary>Tooltip and menu from the latest status; the menu is only rebuilt when something in it changed.</summary>
    internal void Redraw()
    {
        var status = controller.Status;
        icon.ToolTipText = status.Tooltip();
        shown = new Leds(!controller.Leds.Orange, !controller.Leds.Green); // force the icon to be drawn
        Lights();
        var key = string.Join('|', TrayMenu.Build(status).Select(e => $"{e.Command}:{e.Label}:{e.Enabled}"));
        if (key == menuKey) return;
        menuKey = key;
        var menu = new NativeMenu();
        foreach (var entry in TrayMenu.Build(status))
        {
            if (entry.IsSeparator)
            {
                menu.Items.Add(new NativeMenuItemSeparator());
                continue;
            }

            var item = new NativeMenuItem(entry.Label) { IsEnabled = entry.Enabled && entry.Command is not null };
            if (entry.Command is { } command) item.Command = new MenuCommand(() => Do(command));
            menu.Items.Add(item);
        }

        icon.Menu = menu;
    }

    internal async Task Do(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Quit:
                quit();
                return;
            case TrayCommand.ShowWindow:
                Window().Show();
                Window().Activate();
                return;
            default:
                await controller.RunAsync(command, null, CancellationToken.None);
                Redraw();
                return;
        }
    }

    internal StatusWindow Window() => window ??= new StatusWindow(controller, async (command, server) =>
    {
        await controller.RunAsync(command, server, CancellationToken.None);
        Redraw();
    });
}

/// <summary>A menu item's command: runs the given task (the tray's menu cannot await, so the work is started and its failures belong to the controller).</summary>
internal sealed class MenuCommand(Func<Task> run) : System.Windows.Input.ICommand
{
#pragma warning disable CS0067 // the tray menu never asks whether the command can run again
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

    public bool CanExecute(object? parameter) => true;

    public async void Execute(object? parameter) => await run();
}
