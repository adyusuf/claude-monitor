using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Agent.Update;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>A throw-away stand-in for Claude Code's config folder (never the user's): sessions/&lt;pid&gt;.json files the way Claude Code writes them.</summary>
public sealed class SessionsDir : IDisposable
{
    public SessionsDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "cm-claude-test-" + Guid.NewGuid().ToString("N"));
        ConfigDir = Path.Combine(Root, "claude-config");
        Folder = Path.Combine(ConfigDir, "sessions");
    }

    public string Root { get; }
    public string ConfigDir { get; }
    public string Folder { get; }

    public void Create() => Directory.CreateDirectory(Folder);

    public void Write(string file, string content)
    {
        Create();
        File.WriteAllText(Path.Combine(Folder, file), content);
    }

    /// <summary>A session file; a null status or time leaves that field out. Times are epoch milliseconds, as Claude Code writes them.</summary>
    public void Session(int pid, string? status, DateTimeOffset? at, string timeField = "statusUpdatedAt", DateTimeOffset? updatedAt = null)
    {
        var o = new Dictionary<string, object?> { ["pid"] = pid };
        if (status is not null) o["status"] = status;
        if (at is { } t) o[timeField] = t.ToUnixTimeMilliseconds();
        if (updatedAt is { } u) o["updatedAt"] = u.ToUnixTimeMilliseconds();
        Write($"{pid}.json", JsonSerializer.Serialize(o));
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

/// <summary>A clock whose waits cost nothing and move time forward (as <see cref="VirtualClock"/>), and that tells a test when each wait starts.</summary>
public sealed class SteppingClock(DateTimeOffset start) : TimeProvider
{
    private readonly VirtualClock inner = new(start);
    private int ticks;

    /// <summary>Runs when the n-th wait (n from 1) is about to start, so a test can change the world in the middle of a countdown.</summary>
    public Action<int>? OnTick { get; set; }

    public int Ticks => Volatile.Read(ref ticks);
    public TimeSpan Elapsed => inner.Elapsed;

    public void Advance(TimeSpan by) => inner.Advance(by);

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var n = Interlocked.Increment(ref ticks);
        OnTick?.Invoke(n);
        return inner.CreateTimer(callback, state, dueTime, period);
    }
}

/// <summary>Records every command the Claude updater runs and answers `--version` from a script and `update` with an exit code; nothing real starts.</summary>
public sealed class ClaudeRunner : IProcessRunner
{
    private readonly ConcurrentQueue<(int Exit, string Output)> versions = new();

    public ConcurrentQueue<(string File, string[] Args)> Calls { get; } = new();
    public int UpdateExit { get; set; }

    /// <summary>Runs inside `update`: lets a test see the world at the moment the update starts.</summary>
    public Action? OnUpdate { get; set; }

    public IReadOnlyList<string> Asked => [.. Calls.Select(c => string.Join(' ', c.Args))];
    public int Updates => Calls.Count(c => c.Args is ["update"]);

    /// <summary>The next `--version` calls print these, in order (then nothing, exit 0).</summary>
    public void Versions(params string[] outputs)
    {
        foreach (var o in outputs) versions.Enqueue((0, o));
    }

    public void VersionFails() => versions.Enqueue((1, ""));

    public (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args, TimeSpan timeout)
    {
        Calls.Enqueue((file, [.. args]));
        if (args is ["update"])
        {
            OnUpdate?.Invoke();
            return (UpdateExit, "");
        }

        if (args is not ["--version"]) return (-1, "");
        return versions.TryDequeue(out var v) ? v : (0, "");
    }
}

public sealed class FakeNotifier : IUserNotifier
{
    public List<string> Messages { get; } = [];
    public bool Result { get; set; } = true;
    public Action<string>? OnNotify { get; set; }

    public bool Notify(string message)
    {
        Messages.Add(message);
        OnNotify?.Invoke(message);
        return Result;
    }
}

/// <summary>
/// One Claude Code updating machine in a box: a throw-away agent home, a stand-in Claude config folder and `claude` binary
/// (npm layout unless told otherwise), short timings, a runner and a notifier that only record, and a stepping clock.
/// </summary>
public sealed class ClaudeKit : IDisposable
{
    public const string Npm = "tools/node_modules/@anthropic-ai/claude-code/bin/claude.exe";
    public const string Native = "home/.local/share/claude/versions/2.1.0/claude";
    public const string Desktop = "Applications/Claude.app/Contents/Resources/claude";
    public static readonly DateTimeOffset Start = new(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);

    private readonly TempHome home;

    public ClaudeKit(Func<AgentConfig, AgentConfig>? tweak = null, string layout = Npm, bool installed = true)
    {
        Files = new SessionsDir();
        ClaudePath = Path.Combine(Files.Root, layout);
        if (installed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ClaudePath)!);
            File.WriteAllText(ClaudePath, "not a real claude");
        }

        home = new TempHome(c =>
        {
            c = c with
            {
                ClaudeConfigDir = Files.ConfigDir,
                ClaudeBinary = ClaudePath,
                PathVariable = "",
                ClaudeCountdown = TimeSpan.FromSeconds(3),
                ClaudeCountdownPoll = TimeSpan.FromSeconds(1),
                ClaudeSnooze = TimeSpan.FromHours(12),
                UpdateRetryAfter = TimeSpan.FromHours(2),
            };
            return tweak is null ? c : tweak(c);
        });
        Config = home.Config;
        Store = new LocalStore(Config.DatabasePath);
        Log = new AgentLog(Config, Clock);
        Updater = new ClaudeUpdater(Config, Store, Runner, Notifier, Log, Clock, pid => Alive(pid));
    }

    public SessionsDir Files { get; }
    public string ClaudePath { get; }
    public AgentConfig Config { get; }
    public LocalStore Store { get; }
    public SteppingClock Clock { get; } = new(Start);
    public ClaudeRunner Runner { get; } = new();
    public FakeNotifier Notifier { get; } = new();
    public AgentLog Log { get; }
    public ClaudeUpdater Updater { get; }
    public Func<int, bool> Alive { get; set; } = _ => true;

    public ClaudeUpdateState State => ClaudeUpdateState.Load(Config);
    public string LogText => File.Exists(Config.LogPath) ? File.ReadAllText(Config.LogPath) : "";
    public DateTimeOffset Now => Clock.GetUtcNow();

    /// <summary>This machine's consent (saved the way `cm-agent config claude-update` saves it) and the workspace's answer (as the daemon stores it).</summary>
    public void Consent(bool machine, string? workspace)
    {
        SavedSettings.SaveClaudeUpdate(Config, machine);
        if (workspace is not null) TestWorkspace.Set(Config, Store, ClaudePolicy.WorkspaceKey, workspace);
    }

    public void AllowAll() => Consent(machine: true, workspace: "true");

    /// <summary>One live, idle session that has been idle for this long.</summary>
    public void Idle(int pid = 100, TimeSpan? since = null) => Files.Session(pid, "idle", Now - (since ?? TimeSpan.FromMinutes(30)));

    /// <summary>Runs the updater once; every command it ran must be `--version` or `update` of the one `claude` it found, nothing else.</summary>
    public async Task<ClaudeOutcome> RunAsync()
    {
        var outcome = await Updater.RunAsync(CancellationToken.None);
        AssertOnlyVersionAndUpdate();
        return outcome;
    }

    public void AssertOnlyVersionAndUpdate() =>
        Assert.All(Runner.Calls, c =>
        {
            Assert.Equal(ClaudePath, c.File);
            Assert.True(c.Args is ["--version"] or ["update"], $"the agent ran `{string.Join(' ', c.Args)}`");
        });

    public void AssertNextAt(TimeSpan fromNow)
    {
        Assert.NotNull(State.NextAt);
        Assert.Equal(Now + fromNow, DateTimeOffset.Parse(State.NextAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public void AssertNothingHappened(string? code = null)
    {
        Assert.Empty(Runner.Calls);
        Assert.Empty(Notifier.Messages);
        if (code is not null) Assert.Equal(code, State.Result);
    }

    public void Dispose()
    {
        Store.Dispose();
        home.Dispose();
        Files.Dispose();
    }
}
