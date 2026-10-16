using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Exec;

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

    private ScriptScratch(string folder, string scriptPath)
    {
        Folder = folder;
        ScriptPath = scriptPath;
    }

    public string Folder { get; }
    public string ScriptPath { get; }
    public string TempFolder => Path.Combine(Folder, RunEnvironment.TempFolder);

    /// <summary>Makes the folder and writes the script. Throws <see cref="IOException"/> when the folder already exists (a run id is used once).</summary>
    public static ScriptScratch Create(string home, Guid runId, string interpreter, string body)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        ArgumentNullException.ThrowIfNull(body);
        var extension = ScriptInterpreters.Extension(interpreter) ?? throw new ArgumentOutOfRangeException(nameof(interpreter));
        var folder = Path.Combine(home, RunsFolder, runId.ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, RunsFolder));
        if (Directory.Exists(folder)) throw new IOException("the scratch folder of this run already exists");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
        else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var scratch = new ScriptScratch(folder, Path.Combine(folder, ScriptName + extension));
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(scratch.TempFolder);
            else Directory.CreateDirectory(scratch.TempFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(scratch.ScriptPath, options))
            {
                var bytes = new System.Text.UTF8Encoding(false).GetBytes(body);
                stream.Write(bytes);
            }
        }
        catch
        {
            scratch.Dispose();
            throw;
        }

        return scratch;
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
            ScriptInterpreters.Python => ["-I", "-B"],
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
        var runs = Path.Combine(home, RunsFolder);
        if (!Directory.Exists(runs)) return 0;
        var removed = 0;
        foreach (var folder in Directory.EnumerateDirectories(runs))
        {
            if (Guid.TryParseExact(Path.GetFileName(folder), "N", out var id) && isRunning(id)) continue;
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
