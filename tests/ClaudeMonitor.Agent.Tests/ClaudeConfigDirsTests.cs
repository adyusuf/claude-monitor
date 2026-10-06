using System.Text.Json;
using ClaudeMonitor.Agent.Capture;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Storage;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The Claude Code config folders sessions run under: recorded by the hooks, kept normalised, and every one must be idle before an update.</summary>
public sealed class ClaudeConfigDirsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    private readonly List<SessionsDir> dirs = [];
    private readonly TempHome home;
    private readonly LocalStore store;

    public ClaudeConfigDirsTests()
    {
        var own = Make();
        home = new TempHome(c => c with { ClaudeConfigDir = own.ConfigDir });
        store = new LocalStore(home.Config.DatabasePath);
    }

    public void Dispose()
    {
        store.Dispose();
        home.Dispose();
        foreach (var d in dirs) d.Dispose();
    }

    private SessionsDir Make()
    {
        var d = new SessionsDir();
        dirs.Add(d);
        return d;
    }

    // ---- NormalizeDir ------------------------------------------------------------------------------------------

    [Fact]
    public void One_spelling_per_folder_absolute_without_a_trailing_separator_and_without_dots()
    {
        var plain = Path.Combine(Path.GetTempPath(), "cm-norm-test", "cfg");
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal(plain, LocalStore.NormalizeDir(plain + sep));
        Assert.Equal(plain, LocalStore.NormalizeDir(Path.Combine(Path.GetTempPath(), "cm-norm-test", "other", "..", "cfg") + sep));
        var relative = LocalStore.NormalizeDir("cm-relative-cfg");
        Assert.True(Path.IsPathRooted(relative));
        Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), "cm-relative-cfg"), relative);
    }

    // ---- LocalStore --------------------------------------------------------------------------------------------

    [Fact]
    public void A_folder_seen_twice_in_two_spellings_is_one_row_with_the_later_time()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cm-seen-test", "cfg");
        store.ClaudeConfigDirSeen(dir, Now.AddHours(-5));
        store.ClaudeConfigDirSeen(dir + Path.DirectorySeparatorChar, Now);

        Assert.Equal([dir], store.ClaudeConfigDirsSince(Now.AddHours(-1))); // the later time counts, so it is inside this window
        Assert.Single(store.ClaudeConfigDirsSince(DateTimeOffset.MinValue));
    }

    [Fact]
    public void Folders_since_a_time_are_those_seen_at_or_after_it_oldest_first()
    {
        var root = Path.Combine(Path.GetTempPath(), "cm-since-test");
        string[] names = ["a", "b", "c", "d"];
        store.ClaudeConfigDirSeen(Path.Combine(root, "c"), Now.AddHours(-1));
        store.ClaudeConfigDirSeen(Path.Combine(root, "a"), Now.AddHours(-30));
        store.ClaudeConfigDirSeen(Path.Combine(root, "d"), Now);
        store.ClaudeConfigDirSeen(Path.Combine(root, "b"), Now.AddHours(-24));

        Assert.Equal(["b", "c", "d"], store.ClaudeConfigDirsSince(Now - Window).Select(Path.GetFileName)); // exactly on the border is in
        Assert.Equal(names, store.ClaudeConfigDirsSince(DateTimeOffset.MinValue).Select(Path.GetFileName));
        Assert.Empty(store.ClaudeConfigDirsSince(Now.AddSeconds(1)));
    }

    [Fact]
    public void What_a_hook_recorded_is_still_there_after_the_store_is_opened_again()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cm-reopen-test");
        store.ClaudeConfigDirSeen(dir, Now);
        using var again = new LocalStore(home.Config.DatabasePath);
        Assert.Equal([dir], again.ClaudeConfigDirsSince(Now));
    }

    // ---- the hook ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_hook_records_the_config_folder_its_session_runs_under_normalised()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cm-hook-test", "second");
        var config = home.Config with { ClaudeConfigDir = Path.Combine(Path.GetTempPath(), "cm-hook-test", "x", "..", "second") + Path.DirectorySeparatorChar };

        await new HookRunner(config, store, new ManualClock(Now)).RunAsync("SessionStart", JsonSerializer.Serialize(new { session_id = "s9", cwd = home.Dir }), CancellationToken.None);

        Assert.Equal([folder], store.ClaudeConfigDirsSince(Now));
        Assert.Empty(store.ClaudeConfigDirsSince(Now.AddSeconds(1))); // recorded with the hook's own time
    }

    [Fact]
    public async Task Hooks_of_sessions_in_two_folders_record_both()
    {
        var root = Path.Combine(Path.GetTempPath(), "cm-hook-two");
        foreach (var name in new[] { "one", "two" })
        {
            var config = home.Config with { ClaudeConfigDir = Path.Combine(root, name) };
            await new HookRunner(config, store, new ManualClock(Now)).RunAsync("PostToolUse", JsonSerializer.Serialize(new { session_id = name, cwd = home.Dir }), CancellationToken.None);
        }

        Assert.Equal(["one", "two"], store.ClaudeConfigDirsSince(Now).Order().Select(Path.GetFileName));
    }

    // ---- ClaudeSessions.ConfigDirs -----------------------------------------------------------------------------

    [Fact]
    public void The_folders_that_count_are_the_daemons_own_first_then_the_recorded_ones_within_the_window()
    {
        var recent = Make().ConfigDir;
        var old = Make().ConfigDir;
        store.ClaudeConfigDirSeen(recent, Now.AddHours(-2));
        store.ClaudeConfigDirSeen(old, Now - Window - TimeSpan.FromSeconds(1));

        var dirs = ClaudeSessions.ConfigDirs(home.Config, store, Now, Window);

        Assert.Equal([LocalStore.NormalizeDir(home.Config.ClaudeConfigDir), recent], dirs);
        Assert.DoesNotContain(old, dirs);
    }

    [Fact]
    public void The_daemons_own_folder_is_not_listed_twice_even_when_a_hook_recorded_it_in_another_spelling()
    {
        var own = home.Config.ClaudeConfigDir;
        store.ClaudeConfigDirSeen(own + Path.DirectorySeparatorChar, Now);
        Assert.Equal([LocalStore.NormalizeDir(own)], ClaudeSessions.ConfigDirs(home.Config, store, Now, Window));
    }

    [Fact]
    public void A_folder_seen_exactly_the_window_ago_still_counts()
    {
        var edge = Make().ConfigDir;
        store.ClaudeConfigDirSeen(edge, Now - Window);
        Assert.Contains(edge, ClaudeSessions.ConfigDirs(home.Config, store, Now, Window));
    }

    // ---- ClaudeSessions.AllIdle over several folders -----------------------------------------------------------

    private static (bool Idle, string Why) AllIdle(params string[] configDirs) => ClaudeSessions.AllIdle(configDirs, _ => true, Now, TenMinutes);

    [Fact]
    public void An_empty_list_of_folders_is_not_idle()
    {
        Assert.Equal((false, "no Claude config folder is known"), AllIdle());
    }

    [Fact]
    public void A_busy_session_in_the_second_folder_makes_all_not_idle_and_says_which_kind_of_thing_it_was()
    {
        var first = Make();
        var second = Make();
        first.Session(1, "idle", Now.AddMinutes(-30));
        second.Session(2, "busy", Now.AddMinutes(-30));

        Assert.Equal((false, "a session is busy (one of 2 Claude config folders)"), AllIdle(first.ConfigDir, second.ConfigDir));
        Assert.Equal((false, "a session is busy"), AllIdle(second.ConfigDir)); // alone, the folder count is not mentioned
        Assert.Equal((true, "1 session(s), all idle"), AllIdle(first.ConfigDir));
    }

    [Fact]
    public void Idle_sessions_are_counted_across_all_the_folders()
    {
        var first = Make();
        var second = Make();
        first.Session(1, "idle", Now.AddMinutes(-30));
        second.Session(2, "idle", Now.AddMinutes(-30));
        second.Session(3, "idle", Now.AddMinutes(-30));
        Assert.Equal((true, "3 session(s), all idle"), AllIdle(first.ConfigDir, second.ConfigDir));
    }

    [Fact]
    public void Folders_without_any_live_session_are_idle()
    {
        var first = Make();
        var second = Make();
        first.Create();
        second.Create();
        Assert.Equal((true, "no session is running"), AllIdle(first.ConfigDir, second.ConfigDir));
    }

    [Fact]
    public void A_recorded_folder_that_is_missing_makes_all_not_idle()
    {
        var first = Make();
        first.Session(1, "idle", Now.AddMinutes(-30));
        var gone = Path.Combine(Path.GetTempPath(), "cm-gone-" + Guid.NewGuid().ToString("N"));

        var (idle, why) = AllIdle(first.ConfigDir, gone);
        Assert.False(idle);
        Assert.Contains("the sessions folder of Claude Code is not there", why, StringComparison.Ordinal);
        Assert.Contains("(one of 2 Claude config folders)", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_session_file_that_cannot_be_read_in_the_second_folder_makes_all_not_idle()
    {
        var first = Make();
        var second = Make();
        first.Session(1, "idle", Now.AddMinutes(-30));
        second.Write("7.json", "this is not json");

        var (idle, why) = AllIdle(first.ConfigDir, second.ConfigDir);
        Assert.False(idle);
        Assert.Contains("7.json could not be read", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sessions_folder_that_cannot_be_listed_makes_all_not_idle()
    {
        if (OperatingSystem.IsWindows()) return;
        var first = Make();
        var second = Make();
        first.Session(1, "idle", Now.AddMinutes(-30));
        second.Session(2, "idle", Now.AddMinutes(-30));
        File.SetUnixFileMode(second.Folder, UnixFileMode.None);
        try
        {
            var (idle, why) = AllIdle(first.ConfigDir, second.ConfigDir);
            Assert.False(idle);
            Assert.Contains("could not be read", why, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(second.Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void The_first_folder_that_is_not_idle_is_the_one_that_is_reported()
    {
        var first = Make();
        var second = Make();
        first.Session(1, "waiting", Now.AddMinutes(-30));
        second.Session(2, "busy", Now.AddMinutes(-30));
        Assert.Equal("a session is waiting (one of 2 Claude config folders)", AllIdle(first.ConfigDir, second.ConfigDir).Why);
    }

    [Fact]
    public void A_session_idle_for_too_short_a_time_in_the_second_folder_is_not_idle()
    {
        var first = Make();
        var second = Make();
        first.Session(1, "idle", Now.AddMinutes(-30));
        second.Session(2, "idle", Now.AddMinutes(-1));
        Assert.Equal((false, "a session was active less than 10 min ago (one of 2 Claude config folders)"), AllIdle(first.ConfigDir, second.ConfigDir));
    }
}
