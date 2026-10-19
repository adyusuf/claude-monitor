using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Shapes;

namespace Aipod.Tray;

/// <summary>The status window: the version, whether an update is waiting, both lights, the agent's own status lines and the commands as buttons. Closing it only hides it.</summary>
public sealed class StatusWindow : Window
{
    private readonly TrayController controller;
    private readonly Func<TrayCommand, string?, Task> run;
    private readonly TextBlock version = new() { FontSize = 20, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock update = new();
    private readonly TextBlock headline = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock lines = new() { FontFamily = new FontFamily("Menlo,Consolas,monospace"), FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock result = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox server = new() { PlaceholderText = "https://server address (to log in)" };
    private readonly Ellipse orange = Led();
    private readonly Ellipse green = Led();

    public StatusWindow(TrayController controller, Func<TrayCommand, string?, Task> run)
    {
        this.controller = controller;
        this.run = run;
        Title = "aipod";
        Width = 520;
        Height = 560;
        CanResize = true;
        Content = Layout();
        Closing += (_, e) =>
        {
            e.Cancel = true; // the tray keeps running; Quit is in its menu
            Hide();
        };
        controller.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(Render);
        Render();
    }

    /// <summary>Draws the controller's state into the controls.</summary>
    public void Render()
    {
        var status = controller.Status;
        version.Text = $"aipod {status.Version}";
        update.Text = status.UpdateAvailable is { } available ? $"Update {available} is available." : status.Connected ? "Up to date, as far as the last check knows." : "";
        headline.Text = char.ToUpperInvariant(status.Headline.FirstOrDefault()) + (status.Headline.Length > 1 ? status.Headline[1..] : "");
        lines.Text = string.Join(Environment.NewLine, status.Lines);
        result.Text = controller.LastResult;
        if (server.Text.AsSpan().IsWhiteSpace() && status.Server is { } known) server.Text = known;
        var leds = controller.Leds;
        orange.Fill = new SolidColorBrush(leds.Orange ? Color.Parse("#FF9500") : Color.Parse("#784E14"));
        green.Fill = new SolidColorBrush(leds.Green ? Color.Parse("#30D158") : Color.Parse("#185C2C"));
    }

    private StackPanel Layout()
    {
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, command) in new[]
                 {
                     ("Reconnect", TrayCommand.Reconnect), ("Log in", TrayCommand.Login), ("Disconnect", TrayCommand.Disconnect),
                     ("Check for updates", TrayCommand.CheckForUpdate), ("Update now", TrayCommand.UpdateNow),
                     ("Open the web app", TrayCommand.OpenWeb), ("Show the log", TrayCommand.OpenLog),
                 })
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) };
            button.Click += async (_, _) => await run(command, command == TrayCommand.Login ? server.Text : null);
            buttons.Children.Add(button);
        }

        var lights = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        lights.Children.Add(orange);
        lights.Children.Add(new TextBlock { Text = "message in", VerticalAlignment = VerticalAlignment.Center });
        lights.Children.Add(green);
        lights.Children.Add(new TextBlock { Text = "message out", VerticalAlignment = VerticalAlignment.Center });

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        foreach (var child in new Control[] { version, update, lights, headline, new ScrollViewer { Content = lines, MaxHeight = 220 }, server, buttons, result }) panel.Children.Add(child);
        return panel;
    }

    private static Ellipse Led() => new() { Width = 16, Height = 16, Fill = Brushes.Gray };
}
