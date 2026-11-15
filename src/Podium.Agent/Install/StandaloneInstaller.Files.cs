using System.Text;

namespace Podium.Agent.Install;

/// <summary>The file steps of <see cref="StandaloneInstaller"/> (atomic copy and write, delete): split off to keep each file short.</summary>
public sealed partial class StandaloneInstaller
{
    private void CopyAtomic(string from, string to, string what)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.Ordinal)) return;
        var existed = File.Exists(to);
        var temp = to + ".tmp";
        try
        {
            File.Copy(from, temp, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(temp, to, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteFile(temp);
            throw new StepFailedException($"could not {what}: {e.GetType().Name}");
        }

        if (!existed) undo.Add((what, () => DeleteFile(to)));
    }

    private static void WriteAtomic(string path, string text, string what, Encoding? encoding = null)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, text, encoding ?? new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteFile(temp);
            throw new StepFailedException($"could not {what}: {e.GetType().Name}");
        }
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static void DeleteTree(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
