using Podium.Contracts;

namespace Podium.Agent.Exec;

/// <summary>
/// The scratch folder of one script run (ADR-0008 §3c): <c>&lt;home&gt;/runs/&lt;runId&gt;/</c> with the script as <c>script.&lt;ext&gt;</c> (folder 0700, file
/// 0600, never executable) and a <c>tmp</c> folder the run's TMPDIR points at. The script is started from this file as an ordinary argv run,
/// never through <c>sh -c</c>, with the folder as its working directory. Removed when the run ends, without following links; a sweep at start
/// removes what a crash left. The script text is never logged.
/// </summary>
public sealed class ScriptScratch : IDisposable
{
    public const string RunsFolder = "runs";
    public const string ScriptName = "script";
    public const string UnixScriptPath = "/usr/bin:/bin:/usr/sbin:/sbin";

    /// <summary>The stdin text is kept in a file the process's stdin is opened from; a leading dot keeps the name out of the run's own file names (ADR-0010 §2).</summary>
    public const string StdinName = ".stdin";

    private readonly UnixFileMode dirMode;
    private readonly UnixFileMode fileMode;

    private ScriptScratch(string folder, string scriptPath, bool shared)
    {
        Folder = folder;
        ScriptPath = scriptPath;
        // A run account works in a folder it shares with the service (ADR-0010 §5): its group can read and write, nobody else can.
        dirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | (shared ? UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute : 0);
        fileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | (shared ? UnixFileMode.GroupRead | UnixFileMode.GroupWrite : 0);
    }

    public string Folder { get; }
    public string ScriptPath { get; }

    /// <summary>The file the process reads as stdin, or null when the run has no stdin text.</summary>
    public string? StdinPath { get; private set; }

    /// <summary>True when the run carries files next to the script (the interpreter then needs its folder on its path).</summary>
    public bool HasFiles { get; private set; }
    public string TempFolder => Path.Combine(Folder, RunEnvironment.TempFolder);

    /// <summary>Makes the folder and writes the script. Throws <see cref="IOException"/> when the folder already exists (a run id is used once).</summary>
    public static ScriptScratch Create(string home, Guid runId, string interpreter, string body, IReadOnlyList<ScriptFile>? files = null, string? stdin = null, bool shared = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        ArgumentNullException.ThrowIfNull(body);
        var extension = ScriptInterpreters.Extension(interpreter) ?? throw new ArgumentOutOfRangeException(nameof(interpreter));
        // With a run account the scratch folders are made directly in the shared work folder (its setgid keeps their group); otherwise in runs/ of the home.
        var folder = shared ? Path.Combine(home, runId.ToString("N")) : Path.Combine(home, RunsFolder, runId.ToString("N"));
        var scratch = new ScriptScratch(folder, Path.Combine(folder, ScriptName + extension), shared);
        Directory.CreateDirectory(shared ? home : Path.Combine(home, RunsFolder));
        if (Directory.Exists(folder)) throw new IOException("the scratch folder of this run already exists");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
        else scratch.MakeDirectory(folder);
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(scratch.TempFolder);
            else scratch.MakeDirectory(scratch.TempFolder);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = scratch.fileMode;
            using (var stream = new FileStream(scratch.ScriptPath, options))
            {
                var bytes = new System.Text.UTF8Encoding(false).GetBytes(body);
                stream.Write(bytes);
            }

            Chmod(scratch.ScriptPath, scratch.fileMode);
            foreach (var file in files ?? []) scratch.WriteFile(file);
            scratch.HasFiles = files is { Count: > 0 };
            if (stdin is { Length: > 0 })
            {
                scratch.WriteText(Path.Combine(folder, StdinName), stdin);
                scratch.StdinPath = Path.Combine(folder, StdinName);
            }
        }
        catch
        {
            scratch.Dispose();
            throw;
        }

        return scratch;
    }

    /// <summary>Writes one approved file below the folder: its folders 0700, the file 0600 and never executable, nothing that resolves outside the folder.</summary>
    private void WriteFile(ScriptFile file)
    {
        if (!ScriptFiles.IsSafePath(file.Path)) throw new ArgumentException("an unsafe file path", nameof(file));
        var target = Path.GetFullPath(Path.Combine(Folder, file.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ArgumentException("a file outside the run's folder", nameof(file));
        // One level at a time, each made and then given its mode by chmod: a creation mode is cut by the process's umask (a service runs under 0077), a chmod is not.
        var missing = new Stack<string>();
        for (var dir = Path.GetDirectoryName(target)!; dir.Length > Folder.Length && !Directory.Exists(dir); dir = Path.GetDirectoryName(dir)!) missing.Push(dir);
        foreach (var dir in missing) MakeDirectory(dir);
        WriteText(target, file.Text);
    }

    private void MakeDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Chmod(path, dirMode);
    }

    private static void Chmod(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }

    private void WriteText(string path, string text)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = fileMode;
        using (var stream = new FileStream(path, options)) stream.Write(new System.Text.UTF8Encoding(false).GetBytes(text));
        Chmod(path, fileMode);
    }

    /// <summary>
    /// The interpreter's flags and then the script file (ADR-0008 §3c): <c>bash --noprofile --norc</c>, <c>python3 -I -B</c>,
    /// <c>pwsh/powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File</c>; <c>sh</c> takes the file as it is. The arguments follow.
    /// </summary>
    public IReadOnlyList<string> Arguments(string interpreter, IReadOnlyList<string>? args)
    {
        List<string> list = interpreter switch
        {
            ScriptInterpreters.Bash => ["--noprofile", "--norc"],
            // -I keeps the script's own folder off the module path, which a script with files needs; the folder holds only what the owner approved,
            // so -E (no PYTHON* variables) and -s (no user site) stand in for it then.
            ScriptInterpreters.Python => HasFiles ? ["-E", "-s", "-B"] : ["-I", "-B"],
            ScriptInterpreters.PowerShell => ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"],
            _ => [],
        };
        list.Add(ScriptPath);
        if (args is not null) list.AddRange(args);
        return list;
    }

    /// <summary>
    /// The run's environment: the empty one of ADR-0005 with the PATH of root-owned folders only on Unix (decision §9.7) and the temporary
    /// folders inside the scratch folder.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment(string home)
    {
        var env = new Dictionary<string, string>(RunEnvironment.Build(home), StringComparer.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            env["TEMP"] = TempFolder;
            env["TMP"] = TempFolder;
        }
        else
        {
            env["PATH"] = UnixScriptPath;
            env["TMPDIR"] = TempFolder;
        }

        RunEnvironment.AssertClean(env);
        return env;
    }

    /// <summary>Removes the folder; a link inside it is removed, never followed. A folder that is already gone is fine.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the start-up sweep tries again; the script text is not logged
        }
    }

    /// <summary>Removes the scratch folders of runs that are not running (a crash or a kill left them); returns how many it removed.</summary>
    public static int Sweep(string home, Func<Guid, bool> isRunning)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        ArgumentNullException.ThrowIfNull(isRunning);
        return SweepFolder(Path.Combine(home, RunsFolder), isRunning, onlyRunFolders: false);
    }

    /// <summary>
    /// The same for one folder: the shared work folder of a run account (ADR-0010 §5) is swept with <paramref name="onlyRunFolders"/>, so only a folder named like a run
    /// (32 hex digits) is ever removed there.
    /// </summary>
    public static int SweepFolder(string runs, Func<Guid, bool> isRunning, bool onlyRunFolders)
    {
        ArgumentException.ThrowIfNullOrEmpty(runs);
        ArgumentNullException.ThrowIfNull(isRunning);
        if (!Directory.Exists(runs)) return 0;
        var removed = 0;
        foreach (var folder in Directory.EnumerateDirectories(runs))
        {
            var isRun = Guid.TryParseExact(Path.GetFileName(folder), "N", out var id);
            if (onlyRunFolders && !isRun) continue;
            if (isRun && isRunning(id)) continue;
            try
            {
                Directory.Delete(folder, recursive: true);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // left for the next start
            }
        }

        return removed;
    }
}
