using System.Security;
using System.Text;

namespace Aipod.Tray;

/// <summary>
/// Starting the tray when the person logs in. macOS: a LaunchAgent in the user's folder; Windows: a value under the user's Run key. Nothing is written for
/// an admin or for other users, and nothing here asks for elevation. The writes go through <see cref="IAutostartTarget"/> so they are checked without touching the machine.
/// </summary>
public interface IAutostartTarget
{
    void WriteFile(string path, string content);

    void DeleteFile(string path);

    void SetRunValue(string name, string command);

    void DeleteRunValue(string name);
}

/// <summary>Which system the autostart is written for (a parameter, so each way can be checked on any machine).</summary>
public enum AutostartOs
{
    Other,
    MacOs,
    Windows,
}

public static class Autostart
{
    public static AutostartOs Current =>
        OperatingSystem.IsMacOS() ? AutostartOs.MacOs : OperatingSystem.IsWindows() ? AutostartOs.Windows : AutostartOs.Other;

    public const string Label = "net.aipod.tray";
    public const string RunValueName = "aipod-tray";

    public static string PlistPath(string userHome) => Path.Combine(userHome, "Library", "LaunchAgents", Label + ".plist");

    /// <summary>The LaunchAgent: run the tray at login, no restart loop (a quit from the menu stays quit).</summary>
    public static string Plist(string tray)
    {
        var b = new StringBuilder();
        b.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        b.AppendLine("""<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">""");
        b.AppendLine("""<plist version="1.0"><dict>""");
        b.AppendLine($"  <key>Label</key><string>{Label}</string>");
        b.AppendLine($"  <key>ProgramArguments</key><array><string>{SecurityElement.Escape(tray)}</string></array>");
        b.AppendLine("  <key>RunAtLoad</key><true/>");
        b.AppendLine("  <key>KeepAlive</key><false/>");
        b.AppendLine("  <key>ProcessType</key><string>Interactive</string>");
        b.AppendLine("</dict></plist>");
        return b.ToString();
    }

    /// <summary>Turns the start at login on. Returns what was done, in one line.</summary>
    public static string Enable(IAutostartTarget target, string tray, string userHome, AutostartOs? os = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if ((os ?? Current) == AutostartOs.MacOs)
        {
            target.WriteFile(PlistPath(userHome), Plist(tray));
            return $"The tray starts at login ({PlistPath(userHome)}).";
        }

        if ((os ?? Current) == AutostartOs.Windows)
        {
            target.SetRunValue(RunValueName, $"\"{tray}\"");
            return "The tray starts at login (HKCU Run).";
        }

        return "Starting at login is set up for Windows and macOS only.";
    }

    public static string Disable(IAutostartTarget target, string userHome, AutostartOs? os = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if ((os ?? Current) == AutostartOs.MacOs)
        {
            target.DeleteFile(PlistPath(userHome));
            return "The tray no longer starts at login.";
        }

        if ((os ?? Current) == AutostartOs.Windows)
        {
            target.DeleteRunValue(RunValueName);
            return "The tray no longer starts at login.";
        }

        return "Starting at login is set up for Windows and macOS only.";
    }
}

/// <summary>The real target: files under the user's home, the current user's Run key.</summary>
public sealed class SystemAutostartTarget : IAutostartTarget
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public void SetRunValue(string name, string command)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(name, command);
    }

    public void DeleteRunValue(string name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
