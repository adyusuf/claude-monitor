using System.Text.Json;
using Podium.Agent.Auth;
using Podium.Agent.ClaudeUpdate;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.StatusLine;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The usage limits (ADR-0011 §7a): what `aipod statusline` keeps and prints, how the person switches it on and off at the machine, and what the inventory reports.</summary>
public sealed class StatusLineTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private readonly TempHome home = new();
    private readonly SessionsDir files = new();
    private readonly ManualClock clock = new(Start);
    private readonly AgentConfig config;
    private string SettingsPath => Path.Combine(files.ConfigDir, "settings.json");

    public StatusLineTests()
    {
        config = home.Config with { ClaudeConfigDir = files.ConfigDir };
        Directory.CreateDirectory(files.ConfigDir);
    }

    public void Dispose()
    {
        home.Dispose();
        files.Dispose();
    }

    private static string Payload(double five = 23.5, double week = 41.2, long? fiveReset = null, long? weekReset = null) => JsonSerializer.Serialize(new
    {
        model = new { display_name = "Opus" },
        rate_limits = new
        {
            five_hour = new { used_percentage = five, resets_at = fiveReset ?? Start.AddHours(2).ToUnixTimeSeconds() },
            seven_day = new { used_percentage = week, resets_at = weekReset ?? Start.AddDays(3).ToUnixTimeSeconds() },
        },
    });

    private async Task<(int Code, string Out, string Err)> StatusLineAsync(string input)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var code = await StatusLineCommand.RunAsync(config, new StringReader(input), stdout, stderr, clock);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private async Task<(int Code, string Out, string Err)> ConfigAsync(params string[] args)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var code = await StatusLineSetup.RunAsync(["config", "claude-statusline", .. args], config, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    // ---- the snapshot ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_windows_are_read_from_the_payload_with_their_percentage_and_reset_time()
    {
        using var doc = JsonDocument.Parse(Payload());
        var snapshot = LimitsSnapshot.From(doc.RootElement, Start)!;

        Assert.Equal((23.5, 41.2), (snapshot.FiveHour!.UsedPercentage, snapshot.SevenDay!.UsedPercentage));
        Assert.Equal(Start.AddHours(2), snapshot.FiveHour.ResetsAt);
        Assert.Null(snapshot.Spend);
        Assert.Equal("5h 24% · 7d 41%", snapshot.Text());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"rate_limits\":{}}")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":\"high\"}}}")]
    [InlineData("{\"rate_limits\":[1]}")]
    [InlineData("[1,2]")]
    public void A_payload_without_usable_limits_gives_none(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Null(LimitsSnapshot.From(doc.RootElement, Start));
    }

    [Fact]
    public void A_percentage_is_kept_inside_the_range_and_a_missing_reset_time_is_allowed()
    {
        using var doc = JsonDocument.Parse("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":-5},\"spend_limit\":{\"used_percentage\":99999}}}");
        var snapshot = LimitsSnapshot.From(doc.RootElement, Start)!;

        Assert.Equal((0, null), (snapshot.FiveHour!.UsedPercentage, snapshot.FiveHour.ResetsAt));
        Assert.Equal(1000, snapshot.Spend!.UsedPercentage);
    }

    [Fact]
    public void The_snapshot_is_saved_in_the_home_and_read_back_and_a_broken_file_reads_as_none()
    {
        using var doc = JsonDocument.Parse(Payload());
        LimitsSnapshot.From(doc.RootElement, Start)!.Save(config);

        Assert.Equal(23.5, LimitsSnapshot.Load(config)!.FiveHour!.UsedPercentage);
        File.WriteAllText(config.ClaudeLimitsPath, "not json");
        Assert.Null(LimitsSnapshot.Load(config));
        File.WriteAllText(config.ClaudeLimitsPath, new string(' ', 5000));
        Assert.Null(LimitsSnapshot.Load(config));
    }

    // ---- aipod statusline -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_command_keeps_the_limits_and_prints_a_short_line_when_the_person_had_no_status_line()
    {
        var run = await StatusLineAsync(Payload());

        Assert.Equal((0, "5h 24% · 7d 41%", ""), run);
        Assert.Equal(41.2, LimitsSnapshot.Load(config)!.SevenDay!.UsedPercentage);
        Assert.DoesNotContain("Opus", File.ReadAllText(config.ClaudeLimitsPath), StringComparison.Ordinal); // nothing else of the payload is kept
    }

    [Fact]
    public async Task A_payload_without_limits_leaves_the_file_as_it_was_and_still_prints_what_is_known()
    {
        await StatusLineAsync(Payload(five: 10));

        var run = await StatusLineAsync("{\"model\":{\"display_name\":\"Opus\"}}");

        Assert.Equal(0, run.Code);
        Assert.Equal("5h 10% · 7d 41%", run.Out);
        Assert.Equal(10, LimitsSnapshot.Load(config)!.FiveHour!.UsedPercentage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"rate_limits\":")]
    public async Task Whatever_arrives_on_stdin_the_command_exits_zero_and_prints_nothing_when_nothing_is_known(string input)
    {
        var run = await StatusLineAsync(input);

        Assert.Equal((0, "", ""), run);
        Assert.False(File.Exists(config.ClaudeLimitsPath));
    }

    [Fact]
    public async Task With_a_wrapped_status_line_the_command_prints_what_the_old_one_printed_for_the_same_input()
    {
        if (OperatingSystem.IsWindows()) return;
        (Identity.Load(config) with { StatusLineBackup = "{\"type\":\"command\",\"command\":\"echo OLD-$(wc -c | tr -d ' ')\"}" }).Save(config);
        var input = Payload();

        var run = await StatusLineAsync(input);

        Assert.Equal(0, run.Code);
        Assert.Equal($"OLD-{input.Length}", run.Out.Trim());
        Assert.NotNull(LimitsSnapshot.Load(config)); // and the limits were kept all the same
    }

    [Fact]
    public async Task A_wrapped_status_line_that_fails_falls_back_to_the_limits_line()
    {
        if (OperatingSystem.IsWindows()) return;
        (Identity.Load(config) with { StatusLineBackup = "{\"type\":\"command\",\"command\":\"exit 3\"}" }).Save(config);

        var run = await StatusLineAsync(Payload());

        Assert.Equal((0, "5h 24% · 7d 41%"), (run.Code, run.Out));
    }

    // ---- aipod config claude-statusline ---------------------------------------------------------------------------

    [Fact]
    public async Task On_adds_the_status_line_to_the_settings_keeps_every_other_key_and_off_removes_it_again()
    {
        File.WriteAllText(SettingsPath, "{\"theme\":\"dark\",\"hooks\":{\"Stop\":[]}}");

        var on = await ConfigAsync("on");
        using (var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath)))
        {
            var line = doc.RootElement.GetProperty("statusLine");
            Assert.Equal((0, "command", StatusLineSetup.CommandLine(config)), (on.Code, line.GetProperty("type").GetString(), line.GetProperty("command").GetString()));
            Assert.Equal("dark", doc.RootElement.GetProperty("theme").GetString());
            Assert.True(doc.RootElement.GetProperty("hooks").TryGetProperty("Stop", out _));
        }

        var off = await ConfigAsync("off");
        using var after = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal(0, off.Code);
        Assert.False(after.RootElement.TryGetProperty("statusLine", out _));
        Assert.Equal("dark", after.RootElement.GetProperty("theme").GetString());
        Assert.Null(Identity.Load(config).StatusLineBackup);
    }

    [Fact]
    public async Task The_persons_own_status_line_is_kept_wrapped_and_put_back_exactly_by_off()
    {
        File.WriteAllText(SettingsPath, "{\"statusLine\":{\"type\":\"command\",\"command\":\"~/bin/mine.sh\",\"padding\":2,\"refreshInterval\":5}}");

        await ConfigAsync("on");

        using (var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath)))
        {
            Assert.Equal(2, doc.RootElement.GetProperty("statusLine").GetProperty("padding").GetInt32());
            Assert.Equal("~/bin/mine.sh", StatusLineCommand.OriginalCommand(Identity.Load(config).StatusLineBackup!));
        }

        await ConfigAsync("off");
        using var restored = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        var line = restored.RootElement.GetProperty("statusLine");
        Assert.Equal(("~/bin/mine.sh", 2, 5), (line.GetProperty("command").GetString(), line.GetProperty("padding").GetInt32(), line.GetProperty("refreshInterval").GetInt32()));
    }

    [Fact]
    public async Task On_twice_does_not_lose_the_original_and_off_leaves_a_status_line_that_is_no_longer_aipods()
    {
        File.WriteAllText(SettingsPath, "{\"statusLine\":{\"type\":\"command\",\"command\":\"~/bin/mine.sh\"}}");
        await ConfigAsync("on");
        Assert.Contains("already on", (await ConfigAsync("on")).Out, StringComparison.Ordinal);
        Assert.Equal("~/bin/mine.sh", StatusLineCommand.OriginalCommand(Identity.Load(config).StatusLineBackup!));

        File.WriteAllText(SettingsPath, "{\"statusLine\":{\"type\":\"command\",\"command\":\"~/bin/someone-elses.sh\"}}");
        var off = await ConfigAsync("off");

        Assert.Contains("left alone", off.Out, StringComparison.Ordinal);
        Assert.Contains("someone-elses.sh", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("not json")]
    public async Task A_settings_file_that_is_not_a_json_object_is_never_touched(string content)
    {
        File.WriteAllText(SettingsPath, content);

        var run = await ConfigAsync("on");

        Assert.Equal(2, run.Code);
        Assert.Contains("nothing was changed", run.Err, StringComparison.Ordinal);
        Assert.Equal(content, File.ReadAllText(SettingsPath));
        Assert.Null(Identity.Load(config).StatusLineBackup);
    }

    [Fact]
    public async Task With_no_settings_file_on_creates_one_and_off_when_it_was_never_on_changes_nothing()
    {
        Assert.Contains("was not on", (await ConfigAsync("off")).Out, StringComparison.Ordinal);
        Assert.False(File.Exists(SettingsPath));

        await ConfigAsync("on");

        Assert.True(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task A_service_agent_and_an_unknown_argument_change_nothing()
    {
        config.EnsureHome();
        var service = config with { ServiceMode = true };
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        Assert.Equal(2, await StatusLineSetup.RunAsync(["config", "claude-statusline", "on"], service, stdout, stderr));
        Assert.False(File.Exists(SettingsPath));
        Assert.Equal(2, (await ConfigAsync("maybe")).Code);
        Assert.Equal(2, (await ConfigAsync()).Code);
    }

    [Fact]
    public async Task Status_says_whether_it_is_on_and_what_was_seen_last()
    {
        Assert.Contains("claude-statusline: off", (await ConfigAsync("status")).Out, StringComparison.Ordinal);
        Assert.Contains("no limits seen yet", (await ConfigAsync("status")).Out, StringComparison.Ordinal);

        await ConfigAsync("on");
        await StatusLineAsync(Payload());
        var status = (await ConfigAsync("status")).Out;

        Assert.Contains("claude-statusline: on", status, StringComparison.Ordinal);
        Assert.Contains("5h 24%", status, StringComparison.Ordinal);
    }

    // ---- the inventory --------------------------------------------------------------------------------------------

    private sealed class NoCli : IClaudeCli
    {
        public Task<ClaudeRun> RunAsync(string claude, IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(new ClaudeRun(0, "[]", ""));
    }

    private ClaudeInventoryReader Reader() => new(config, new NoCli(), () => new ClaudeInstall(ClaudeKind.Npm, "/usr/local/bin/claude", ""), _ => true);

    [Fact]
    public async Task The_inventory_carries_the_limits_with_the_time_they_were_seen_and_drops_a_window_that_has_reset()
    {
        using var doc = JsonDocument.Parse(Payload(fiveReset: Start.AddMinutes(-1).ToUnixTimeSeconds()));
        LimitsSnapshot.From(doc.RootElement, Start.AddMinutes(-10))!.Save(config);

        var inventory = await Reader().ReadAsync([files.ConfigDir], [], Start, CancellationToken.None);

        Assert.Null(inventory.Limits!.FiveHour); // its window ended a minute ago
        Assert.Equal(41.2, inventory.Limits.SevenDay!.UsedPercentage);
        Assert.Equal(Start.AddMinutes(-10), inventory.Limits.SeenAt);
    }

    [Fact]
    public async Task The_inventory_has_no_limits_when_none_was_seen_or_every_window_has_reset()
    {
        Assert.Null((await Reader().ReadAsync([files.ConfigDir], [], Start, CancellationToken.None)).Limits);

        using var doc = JsonDocument.Parse(Payload(fiveReset: Start.AddMinutes(-1).ToUnixTimeSeconds(), weekReset: Start.AddMinutes(-1).ToUnixTimeSeconds()));
        LimitsSnapshot.From(doc.RootElement, Start.AddHours(-1))!.Save(config);

        Assert.Null((await Reader().ReadAsync([files.ConfigDir], [], Start, CancellationToken.None)).Limits);
    }
}
