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
            new(null, $"aipod {status.Version}", false),
            new(null, StateLine(status), false),
        };
        if (status.UpdateAvailable is { } update) entries.Add(new MenuEntry(null, $"Update {update} is available", false));
        entries.Add(separator);
        entries.Add(new MenuEntry(TrayCommand.ShowWindow, "Open the status window"));
        entries.Add(separator);
        entries.Add(new MenuEntry(TrayCommand.Reconnect, "Reconnect", status.Connected));
        entries.Add(new MenuEntry(TrayCommand.Login, status.Connected ? "Log in again…" : "Log in…"));
        entries.Add(new MenuEntry(TrayCommand.Disconnect, "Disconnect", status.Connected));
        entries.Add(separator);
        entries.Add(new MenuEntry(TrayCommand.CheckForUpdate, "Check for updates", status.Connected));
        entries.Add(new MenuEntry(TrayCommand.UpdateNow, status.UpdateAvailable is { } v ? $"Update to {v}" : "Update now", status.Connected && status.UpdateAvailable is not null));
        entries.Add(separator);
        entries.Add(new MenuEntry(TrayCommand.OpenWeb, "Open the web app", status.Server is not null));
        entries.Add(new MenuEntry(TrayCommand.OpenLog, "Show the log"));
        entries.Add(separator);
        entries.Add(new MenuEntry(TrayCommand.Quit, "Quit the tray"));
        return entries;
    }

    private static string StateLine(TrayStatus status)
    {
        var host = status.Server is { } server && Uri.TryCreate(server, UriKind.Absolute, out var uri) ? $" ({uri.Host})" : "";
        return char.ToUpperInvariant(status.Headline[0]) + status.Headline[1..] + host;
    }
}
