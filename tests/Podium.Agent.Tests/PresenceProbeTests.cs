using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>What the agent reports about Claude Code (ADR-0008 §3e): the install kind, the cached version and the live and idle sessions.</summary>
public sealed class PresenceProbeTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    private const int DeadPid = 2_000_000_000;

    private readonly SessionsDir files = new(); // a stand-in Claude config folder and install root, never the user's
    private readonly TempHome home;
    private readonly LocalStore store;
    private readonly List<string> asked = [];
    private string? answer = "2.1.300 (Claude Code)";

    public PresenceProbeTests()
    {
        home = new TempHome(c => c with { ClaudeConfigDir = files.ConfigDir, PathVariable = "", PresenceProbeEvery = TimeSpan.FromHours(6) });
        store = new LocalStore(home.Config.DatabasePath);
    }

    public void Dispose()
    {
        store.Dispose();
        home.Dispose();
        files.Dispose();
    }

    private string Install(string layout)
    {
        var path = Path.Combine(files.Root, layout);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not a real claude");
        return path;
    }

    private ClaudePresence Probe(string? binary, DateTimeOffset? at = null) =>
        PresenceProbe.Probe(home.Config with { ClaudeBinary = binary }, store, p => { asked.Add(p); return answer; }, at ?? Start);

    [Fact]
    public void A_native_install_is_reported_with_its_path_and_the_version_read_from_the_output()
    {
        var path = Install(ClaudeKit.Native);

        var presence = Probe(path);

        Assert.Equal((ClaudeKinds.Native, path, "2.1.300", Start), (presence.Kind, presence.Path, presence.Version, presence.SeenAt));
        Assert.Equal([path], asked);
    }

    [Fact]
    public void An_npm_install_is_reported_as_npm_and_the_version_is_the_first_dotted_number_of_the_line()
    {
        answer = "claude code v10.20.30 build 4.5.6";

        var presence = Probe(Install(ClaudeKit.Npm));

        Assert.Equal((ClaudeKinds.Npm, "10.20.30"), (presence.Kind, presence.Version));
    }

    [Fact]
    public void A_desktop_app_copy_is_unsupported_but_its_path_and_version_are_still_reported()
    {
        var presence = Probe(Install(ClaudeKit.Desktop));

        Assert.Equal((ClaudeKinds.Unsupported, "2.1.300"), (presence.Kind, presence.Version));
    }

    [Fact]
    public void Without_a_claude_the_kind_is_none_with_no_path_no_version_and_nothing_is_run()
    {
        var presence = Probe(null);

        Assert.Equal((ClaudeKinds.None, null, null), (presence.Kind, presence.Path, presence.Version));
        Assert.Empty(asked);
        Assert.Null(store.Get(PresenceProbe.VersionKey));

        Assert.Equal(ClaudeKinds.None, Probe(Path.Combine(files.Root, "no", "such", "claude")).Kind);
    }

    [Fact]
    public void An_answer_without_a_version_number_is_a_null_version()
    {
        answer = "command not found";
        Assert.Null(Probe(Install(ClaudeKit.Native)).Version);

        answer = null;
        Assert.Null(Probe(Install(ClaudeKit.Native), Start.AddDays(2)).Version);
    }

    [Fact]
    public void The_version_is_kept_in_the_local_store_under_the_presence_keys()
    {
        var path = Install(ClaudeKit.Native);
        Probe(path);

        Assert.Equal("2.1.300", store.Get(PresenceProbe.VersionKey));
        Assert.Equal(path, store.Get(PresenceProbe.VersionPathKey));
        Assert.False(string.IsNullOrEmpty(store.Get(PresenceProbe.VersionMtimeKey)));
        Assert.Equal(Start, DateTimeOffset.Parse(store.Get(PresenceProbe.VersionAtKey)!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.All(new[] { PresenceProbe.VersionKey, PresenceProbe.VersionPathKey, PresenceProbe.VersionMtimeKey, PresenceProbe.VersionAtKey },
            k => Assert.StartsWith("presence.", k, StringComparison.Ordinal));
    }

    [Fact]
    public void A_second_probe_within_the_interval_answers_from_the_store_without_running_claude()
    {
        var path = Install(ClaudeKit.Native);
        Probe(path);
        answer = "9.9.9"; // would show if it were read again

        var again = Probe(path, Start.AddHours(5));

        Assert.Equal("2.1.300", again.Version);
        Assert.Single(asked);
    }

    [Fact]
    public void A_changed_binary_or_a_passed_interval_or_a_clock_gone_back_reads_the_version_again()
    {
        var path = Install(ClaudeKit.Native);
        Probe(path);

        answer = "2.2.0";
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(7)); // the binary was replaced
        Assert.Equal("2.2.0", Probe(path, Start.AddMinutes(1)).Version);
        Assert.Equal(2, asked.Count);

        answer = "2.3.0";
        Assert.Equal("2.3.0", Probe(path, Start.AddMinutes(1).AddHours(6)).Version); // the interval is over
        Assert.Equal(3, asked.Count);

        answer = "2.4.0";
        Assert.Equal("2.4.0", Probe(path, Start).Version); // now is before the stored time: not trusted
        Assert.Equal(4, asked.Count);
    }

    [Fact]
    public void Another_claude_path_is_not_served_from_the_cache_of_the_previous_one()
    {
        Probe(Install(ClaudeKit.Native));
        answer = "3.0.0";

        Assert.Equal("3.0.0", Probe(Install(ClaudeKit.Npm), Start.AddMinutes(1)).Version);
    }

    [Fact]
    public void Sessions_counts_live_and_idle_ones_across_the_daemons_folder_and_a_folder_a_hook_recorded()
    {
        var other = new SessionsDir();
        try
        {
            files.Session(Environment.ProcessId, "idle", Start.AddMinutes(-10)); // this very process stands in for a live session
            files.Session(DeadPid, "idle", Start.AddMinutes(-10)); // a dead one is not counted
            other.Session(Environment.ProcessId, "busy", Start);
            store.ClaudeConfigDirSeen(other.ConfigDir, Start.AddHours(-2));

            Assert.Equal((2, 1), PresenceProbe.Sessions(home.Config, store, Start));
            var presence = Probe(null);
            Assert.Equal((2, 1), (presence.SessionsLive, presence.SessionsIdle));
        }
        finally
        {
            other.Dispose();
        }
    }

    [Fact]
    public void A_folder_seen_more_than_a_day_ago_a_missing_folder_and_a_broken_file_count_for_nothing()
    {
        var old = new SessionsDir();
        try
        {
            old.Session(Environment.ProcessId, "idle", Start);
            store.ClaudeConfigDirSeen(old.ConfigDir, Start.AddDays(-2));
            Assert.Equal((0, 0), PresenceProbe.Sessions(home.Config, store, Start)); // the daemon's own folder does not exist yet

            files.Write("broken.json", "{not json");
            Assert.Equal((0, 0), PresenceProbe.Sessions(home.Config, store, Start));
        }
        finally
        {
            old.Dispose();
        }
    }
}
