using ClaudeMonitor.Agent.ClaudeUpdate;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>Which Claude Code sessions run and whether all are idle: every doubt reads as "not idle".</summary>
public sealed class ClaudeSessionsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    private readonly SessionsDir files = new();
    private readonly TempHome home;

    public ClaudeSessionsTests() => home = new TempHome(c => c with { ClaudeConfigDir = files.ConfigDir });

    public void Dispose()
    {
        home.Dispose();
        files.Dispose();
    }

    private (bool Idle, string Why) Check(Func<int, bool>? alive = null, TimeSpan? idleFor = null) =>
        ClaudeSessions.AllIdle(ClaudeSessions.Read(home.Config, alive ?? (_ => true)), Now, idleFor ?? TenMinutes);

    [Fact]
    public void A_live_idle_session_that_has_been_idle_long_enough_is_idle()
    {
        files.Session(100, "idle", Now.AddMinutes(-30));
        var view = ClaudeSessions.Read(home.Config, _ => true);
        Assert.True(view.Readable);
        Assert.Equal([100], view.Live.Select(s => s.Pid));
        Assert.Equal((true, "1 session(s), all idle"), Check());
    }

    [Fact]
    public void No_live_session_at_all_is_idle_when_the_folder_could_be_read()
    {
        files.Create();
        Assert.Equal((true, "no session is running"), Check());
    }

    [Fact]
    public void A_session_whose_process_is_gone_is_ignored_even_when_its_file_says_busy()
    {
        files.Session(111, "busy", Now.AddMinutes(-30));
        files.Session(222, "idle", Now.AddMinutes(-30));
        var view = ClaudeSessions.Read(home.Config, pid => pid != 111);
        Assert.Equal([222], view.Live.Select(s => s.Pid));
        Assert.True(Check(pid => pid != 111).Idle);
        Assert.False(Check().Idle, "while that process lives, the busy file counts");
    }

    [Fact]
    public void The_default_liveness_check_asks_the_operating_system()
    {
        files.Session(Environment.ProcessId, "busy", Now.AddMinutes(-30)); // this test process certainly runs
        files.Session(int.MaxValue - 1, "busy", Now.AddMinutes(-30)); // no such process
        var view = ClaudeSessions.Read(home.Config);
        Assert.Equal([Environment.ProcessId], view.Live.Select(s => s.Pid));
    }

    [Theory]
    [InlineData("busy", "a session is busy")]
    [InlineData("waiting", "a session is waiting")]
    [InlineData("thinking", "a session is in a state this agent does not know")]
    [InlineData("IDLE", "a session is in a state this agent does not know")]
    [InlineData(null, "a session is in a state this agent does not know")]
    public void Anything_but_exactly_idle_is_not_idle_and_says_why(string? status, string why)
    {
        files.Session(100, status, Now.AddMinutes(-30));
        Assert.Equal((false, why), Check());
    }

    [Fact]
    public void One_busy_session_among_idle_ones_makes_all_not_idle()
    {
        files.Session(1, "idle", Now.AddMinutes(-30));
        files.Session(2, "busy", Now.AddMinutes(-30));
        files.Session(3, "idle", Now.AddMinutes(-30));
        Assert.False(Check().Idle);
    }

    [Fact]
    public void Idle_for_less_than_the_required_time_is_not_idle_and_the_time_is_epoch_milliseconds()
    {
        files.Session(100, "idle", Now.AddSeconds(-5));
        Assert.Equal((false, "a session was active less than 10 min ago"), Check());
        files.Session(100, "idle", Now.AddMinutes(-30));
        Assert.True(Check().Idle);
        files.Session(100, "idle", Now.AddMinutes(-9));
        Assert.False(Check().Idle);
        files.Session(100, "idle", Now.AddMinutes(-10));
        Assert.True(Check().Idle, "exactly the required time is enough");
        Assert.False(Check(idleFor: TimeSpan.FromHours(1)).Idle, "the required time is the configured one");
    }

    [Fact]
    public void The_time_of_the_status_wins_and_updatedAt_is_the_fallback_and_without_either_it_is_not_idle()
    {
        files.Session(100, "idle", null, updatedAt: Now.AddMinutes(-30));
        Assert.True(Check().Idle, "no statusUpdatedAt: updatedAt is used");
        files.Session(100, "idle", Now.AddSeconds(-5), updatedAt: Now.AddMinutes(-30));
        Assert.False(Check().Idle, "statusUpdatedAt wins over an older updatedAt");
        files.Session(100, "idle", Now.AddMinutes(-30), updatedAt: Now.AddSeconds(-5));
        Assert.True(Check().Idle, "statusUpdatedAt wins over a newer updatedAt");
        files.Session(100, "idle", null);
        Assert.Equal((false, "a session was active less than 10 min ago"), Check());
        files.Write("100.json", """{"pid":100,"status":"idle","statusUpdatedAt":0,"updatedAt":-5}""");
        Assert.False(Check().Idle, "zero and negative times are no time");
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("""{"status":"idle","statusUpdatedAt":1}""")]
    [InlineData("""{"pid":"100","status":"idle"}""")]
    [InlineData("""[{"pid":100}]""")]
    public void A_file_that_cannot_be_read_makes_everything_not_idle_even_next_to_an_idle_session(string content)
    {
        files.Session(100, "idle", Now.AddMinutes(-30));
        files.Write("200.json", content);
        var view = ClaudeSessions.Read(home.Config, _ => true);
        Assert.False(view.Readable);
        Assert.Empty(view.Live);
        Assert.Contains("200.json could not be read", view.Why, StringComparison.Ordinal);
        Assert.Equal((false, view.Why), Check());
    }

    [Theory]
    [InlineData("""{"pid":100,"status":"idle","statusUpdatedAt":"1759744800000"}""")]
    [InlineData("""{"pid":100,"status":"idle","statusUpdatedAt":true}""")]
    [InlineData("""{"pid":100,"status":"idle","statusUpdatedAt":1.5e300}""")]
    [InlineData("""{"pid":100,"status":"idle","updatedAt":"yesterday"}""")]
    [InlineData("""{"pid":100.5,"status":"idle","statusUpdatedAt":1}""")]
    [InlineData("""{"pid":100,"status":["idle"],"statusUpdatedAt":1}""")]
    public void A_field_of_the_wrong_type_never_throws_and_is_never_idle(string content)
    {
        files.Write("100.json", content);
        var (idle, _) = Check(); // must not throw: the updater would die with it
        Assert.False(idle);
    }

    [Fact]
    public void A_missing_sessions_folder_is_not_idle_because_nothing_can_be_seen()
    {
        Assert.False(Directory.Exists(files.Folder));
        var view = ClaudeSessions.Read(home.Config, _ => true);
        Assert.False(view.Readable);
        var (idle, why) = Check();
        Assert.False(idle);
        Assert.Contains("sessions folder", why, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_that_are_not_json_are_ignored()
    {
        files.Session(100, "idle", Now.AddMinutes(-30));
        files.Write("100.key", "garbage {{{");
        files.Write("notes.txt", """{"pid":999,"status":"busy"}""");
        var view = ClaudeSessions.Read(home.Config, _ => true);
        Assert.True(view.Readable);
        Assert.Equal([100], view.Live.Select(s => s.Pid));
        Assert.True(Check().Idle);
    }
}
