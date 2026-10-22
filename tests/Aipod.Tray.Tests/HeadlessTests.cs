using Aipod.Tray;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Aipod.Tray.Tests;

/// <summary>The one headless Avalonia application of the test run: the tray's own <see cref="App"/> over a temp agent home and a fake command line.</summary>
public static class TestHost
{
    public static readonly AgentHome Home = new();
    public static readonly FakeCli Cli = new();
    public static readonly ManualClock Clock = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
    public static readonly TrayController Controller = new(new StatusReader(Home.Config, Clock), new TrayActions(Home.Config, Cli), new LedBlinker(Clock));
    public static int Quits { get; private set; }

    public static void Quit() => Quits++;

    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(TestApp)));

    public static Task Run(Action action) => Session.Value.Dispatch(action, CancellationToken.None);

    /// <summary>Runs async work on the UI thread and waits for all of it (the session has no overload for a plain Task).</summary>
    public static Task Run(Func<Task> action) => Session.Value.Dispatch<bool>(async () =>
    {
        await action();
        return true;
    }, CancellationToken.None);
}

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure(() => new App(TestHost.Controller, TestHost.Quit)).UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class WindowTests
{
    private static IEnumerable<Button> Buttons(StatusWindow window) => window.GetVisualDescendants().OfType<Button>();

    private static Button Button(StatusWindow window, string label) => Buttons(window).Single(b => (string?)b.Content == label);

    private static string AllText(StatusWindow window) => string.Join("\n", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

    [Fact]
    public async Task The_window_shows_the_version_the_update_line_the_status_lines_and_the_server_it_knows()
    {
        TestHost.Home.Connect("https://aipod.example.test");
        Podium.Agent.Update.UpdateState.Change(TestHost.Home.Config, s => s with { Available = "0.9.0" });
        await TestHost.Run(() =>
        {
            TestHost.Controller.Refresh();
            var window = new StatusWindow(TestHost.Controller, (_, _) => Task.CompletedTask);
            window.Show();

            var text = AllText(window);
            Assert.Contains("aipod ", text, StringComparison.Ordinal);
            Assert.Contains("Update 0.9.0 is available.", text, StringComparison.Ordinal);
            Assert.Contains("message in", text, StringComparison.Ordinal);
            Assert.Equal("https://aipod.example.test", window.GetVisualDescendants().OfType<TextBox>().Single().Text);
            Assert.Equal(7, Buttons(window).Count());
            window.Close();
        });
    }

    [Fact]
    public async Task Every_button_runs_its_command_and_log_in_passes_the_address_typed_in()
    {
        var asked = new List<(TrayCommand, string?)>();
        await TestHost.Run(() =>
        {
            var window = new StatusWindow(TestHost.Controller, (command, server) =>
            {
                asked.Add((command, server));
                return Task.CompletedTask;
            });
            window.Show();
            window.GetVisualDescendants().OfType<TextBox>().Single().Text = "https://typed.example.test";

            foreach (var (label, _) in new[]
                     {
                         ("Reconnect", 0), ("Log in", 0), ("Disconnect", 0), ("Check for updates", 0), ("Update now", 0), ("Open the web app", 0), ("Show the log", 0),
                     })
            {
                Button(window, label).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            }

            window.Close();
        });

        Assert.Equal(
        [
            (TrayCommand.Reconnect, null), (TrayCommand.Login, "https://typed.example.test"), (TrayCommand.Disconnect, null),
            (TrayCommand.CheckForUpdate, null), (TrayCommand.UpdateNow, null), (TrayCommand.OpenWeb, null), (TrayCommand.OpenLog, null),
        ], asked);
    }

    [Fact]
    public async Task Closing_the_window_only_hides_it_and_it_can_be_shown_again()
    {
        await TestHost.Run(() =>
        {
            var window = new StatusWindow(TestHost.Controller, (_, _) => Task.CompletedTask);
            window.Show();
            Assert.True(window.IsVisible);

            window.Close();

            Assert.False(window.IsVisible);
            window.Show();
            Assert.True(window.IsVisible);
            window.Close();
        });
    }

    [Fact]
    public async Task The_lights_in_the_window_follow_the_controller()
    {
        await TestHost.Run(() =>
        {
            var window = new StatusWindow(TestHost.Controller, (_, _) => Task.CompletedTask);
            window.Show();
            var leds = window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().ToList();
            Assert.Equal(2, leds.Count);
            var dimOrange = ((Avalonia.Media.SolidColorBrush)leds[0].Fill!).Color;

            using (var store = new Podium.Agent.Storage.LocalStore(TestHost.Home.Config.DatabasePath))
            {
                store.Bump(Podium.Agent.Push.PushStatus.InCountKey);
            }

            TestHost.Controller.RefreshLights();
            window.Render();

            Assert.NotEqual(dimOrange, ((Avalonia.Media.SolidColorBrush)leds[0].Fill!).Color);
            window.Close();
        });
    }
}

public sealed class AppTests
{
    [Fact]
    public async Task The_tray_icon_has_a_tooltip_an_icon_and_the_menu_of_the_status()
    {
        await TestHost.Run(() =>
        {
            var app = (App)Application.Current!;
            app.Redraw();

            Assert.StartsWith("aipod ", app.Icon.ToolTipText, StringComparison.Ordinal);
            Assert.NotNull(app.Icon.Icon);
            var labels = app.Icon.Menu!.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToList();
            Assert.Contains("Open the status window", labels);
            Assert.Contains("Quit the tray", labels);
            Assert.Contains(app.Icon.Menu.Items, i => i is NativeMenuItemSeparator);
        });
    }

    [Fact]
    public async Task Menu_items_run_their_commands_quit_asks_the_host_to_stop_and_the_window_opens_once()
    {
        TestHost.Home.Connect("https://aipod.example.test");
        TestHost.Controller.Refresh();
        TestHost.Cli.Runs.Clear();
        var quits = TestHost.Quits;
        await TestHost.Run(async () =>
        {
            var app = (App)Application.Current!;
            app.Redraw();
            var items = app.Icon.Menu!.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).ToDictionary(i => i.Header!);

            await app.Do(TrayCommand.Reconnect);
            items["Quit the tray"].Command!.Execute(null);
            await app.Do(TrayCommand.ShowWindow);
            var window = app.Window();
            await app.Do(TrayCommand.ShowWindow);

            Assert.Same(window, app.Window());
            Assert.True(window.IsVisible);
            window.Close();
            Assert.True(items["Quit the tray"].Command!.CanExecute(null));
        });

        Assert.Equal(["restart"], TestHost.Cli.Runs.Select(r => string.Join(' ', r)).Take(1));
        Assert.Equal(quits + 1, TestHost.Quits);
    }

    [Fact]
    public async Task A_menu_item_that_is_a_command_runs_it_and_the_menu_is_rebuilt_only_when_something_changed()
    {
        await TestHost.Run(async () =>
        {
            var app = (App)Application.Current!;
            app.Redraw();
            var first = app.Icon.Menu;
            app.Redraw();
            Assert.Same(first, app.Icon.Menu); // nothing changed: the same menu

            TestHost.Cli.Runs.Clear();
            var update = app.Icon.Menu!.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Check for updates");
            update.Command!.Execute(null);
            await Task.Delay(100);
            Assert.Equal("update --check", string.Join(' ', TestHost.Cli.Runs.Single()));
        });
    }

    [Fact]
    public async Task The_icon_is_drawn_again_only_when_the_lights_changed()
    {
        await TestHost.Run(() =>
        {
            var app = (App)Application.Current!;
            app.Redraw();
            var before = app.Icon.Icon;
            app.Lights(); // same lights: the same icon
            Assert.Same(before, app.Icon.Icon);

            using (var store = new Podium.Agent.Storage.LocalStore(TestHost.Home.Config.DatabasePath))
            {
                store.Bump(Podium.Agent.Push.PushStatus.OutCountKey);
            }

            TestHost.Controller.RefreshLights();
            app.Lights();
            Assert.NotSame(before, app.Icon.Icon);
        });
    }
}
