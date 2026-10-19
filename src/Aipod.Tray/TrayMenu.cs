using ClaudeMonitor.Agent.Config;
namespace Aipod.Tray;

/// <summary>Everything the menu and the window's buttons can ask for.</summary>
public enum TrayCommand
{
    ShowWindow,
    Reconnect,
    Login,
    Disconnect,
    CheckForUpdate,
    UpdateNow,
    OpenWeb,
    OpenLog,
    Quit,
}

/// <summary>One line of the menu: a command, a disabled label, or (Command null and Label empty) a separator.</summary>
public sealed record MenuEntry(TrayCommand? Command, string Label, bool Enabled = true)
{
    public bool IsSeparator => Command is null && Label.Length == 0;
}

public static class TrayMenu
{
    /// <summary>The menu for a status: the version and state as disabled lines on top, then the commands that make sense now.</summary>
    public static IReadOnlyList<MenuEntry> Build(TrayStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var separator = new MenuEntry(null, "");
        var entries = new List<MenuEntry>
        {
            new(null, Strings.T(Msg.TrayVersion, status.Version), false),
            new(null, StateLine(status), false),
        };
        if (status.UpdateAvailable is { } update) entries.Add(new MenuEntry(null, Strings.T(Msg.TrayUpdateAvailable, update), false));
        entries.Add(separator);
        entries.Add(new MenuEntry(TrayCommand.ShowWindow, Strings.T(Msg.TrayOpenWindow)));
        entries.Add(separator);
        entries.Add(new MenuEntry(TrayCommand.Reconnect, Strings.T(Msg.TrayReconnect), status.Connected));
        entries.Add(new MenuEntry(TrayCommand.Login, Strings.T(status.Connected ? Msg.TrayLoginAgain : Msg.TrayLoginDots)));
        entries.Add(new MenuEntry(TrayCommand.Disconnect, Strings.T(Msg.TrayDisconnect), status.Connected));
        entries.Add(separator);
        entries.Add(new MenuEntry(TrayCommand.CheckForUpdate, Strings.T(Msg.TrayCheckUpdates), status.Connected));
        entries.Add(new MenuEntry(TrayCommand.UpdateNow, status.UpdateAvailable is { } v ? Strings.T(Msg.TrayUpdateTo, v) : Strings.T(Msg.TrayUpdateNow), status.Connected && status.UpdateAvailable is not null));
        entries.Add(separator);
        entries.Add(new MenuEntry(TrayCommand.OpenWeb, Strings.T(Msg.TrayOpenWeb), status.Server is not null));
        entries.Add(new MenuEntry(TrayCommand.OpenLog, Strings.T(Msg.TrayShowLog)));
        entries.Add(separator);
        entries.Add(new MenuEntry(TrayCommand.Quit, Strings.T(Msg.TrayQuit)));
        return entries;
    }

    private static string StateLine(TrayStatus status)
    {
        var host = status.Server is { } server && Uri.TryCreate(server, UriKind.Absolute, out var uri) ? $" ({uri.Host})" : "";
        return char.ToUpperInvariant(status.Headline[0]) + status.Headline[1..] + host;
    }
}
