using System.Globalization;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Storage;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>`cm-agent claude-update cancel`, `cm-agent config claude-update on|off` and the lines `cm-agent status` shows, driven through Cli.</summary>
public sealed class ClaudeCommandTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 14, 30, 0, TimeSpan.Zero);

    private readonly TempHome home = new();
    private readonly ManualClock clock = new(Now);

    public void Dispose() => home.Dispose();

    private async Task<(int Code, string Out, string Err)> Cli_(params string[] args)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var code = await Cli.RunAsync(args, home.Config, new StringReader(""), stdout, stderr, clock);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private void Workspace(string value)
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        TestWorkspace.Set(home.Config, store, ClaudePolicy.WorkspaceKey, value);
    }

    private void State(ClaudeUpdateState state) => ClaudeUpdateState.Change(home.Config, _ => state);

    private static string Iso(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    private static string Local(DateTimeOffset at, string format) => at.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);

    // ---- cancel ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_while_a_countdown_is_pending_writes_the_cancel_file_and_exits_0()
    {
        State(new ClaudeUpdateState(CountdownUntil: Iso(Now.AddMinutes(3))));
        var (code, output, error) = await Cli_("claude-update", "cancel");
        Assert.Equal(0, code);
        Assert.Contains("cancelled", output, StringComparison.Ordinal);
        Assert.Equal("", error);
        Assert.True(File.Exists(home.Config.ClaudeCancelPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task Cancel_with_no_countdown_pending_says_so_and_writes_nothing(int? minutesFromNow)
    {
        if (minutesFromNow is { } m) State(new ClaudeUpdateState(CountdownUntil: Iso(Now.AddMinutes(m))));
        var (code, output, _) = await Cli_("claude-update", "cancel");
        Assert.Equal(0, code);
        Assert.Contains("no Claude Code update is waiting", output, StringComparison.Ordinal);
        Assert.False(File.Exists(home.Config.ClaudeCancelPath));
    }

    [Fact]
    public async Task Cancel_with_a_state_that_has_no_countdown_writes_nothing()
    {
        State(new ClaudeUpdateState(Result: ClaudeCodes.Updated, NextAt: Iso(Now.AddHours(5))));
        Assert.Contains("no Claude Code update is waiting", (await Cli_("claude-update", "cancel")).Out, StringComparison.Ordinal);
        Assert.False(File.Exists(home.Config.ClaudeCancelPath));
    }

    [Theory]
    [InlineData("claude-update")]
    [InlineData("claude-update", "stop")]
    [InlineData("claude-update", "CANCEL")]
    [InlineData("claude-update", "cancel", "now")]
    public async Task Cancel_with_anything_else_prints_the_usage_and_exits_2(params string[] args)
    {
        State(new ClaudeUpdateState(CountdownUntil: Iso(Now.AddMinutes(3))));
        var (code, output, error) = await Cli_(args);
        Assert.Equal(2, code);
        Assert.Contains("usage: cm-agent claude-update cancel", error, StringComparison.Ordinal);
        Assert.Equal("", output);
        Assert.False(File.Exists(home.Config.ClaudeCancelPath));
    }

    // ---- config ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    public async Task Config_claude_update_saves_to_agent_json_and_says_what_it_did(string value, bool saved)
    {
        var (code, output, _) = await Cli_("config", "claude-update", value);
        Assert.Equal(0, code);
        Assert.Contains($"claude-update: {value} on this machine", output, StringComparison.Ordinal);
        Assert.Contains("workspace must allow it too", output, StringComparison.Ordinal);
        Assert.Equal(saved, Identity.Peek(home.Config)!.ClaudeUpdate);
        Assert.Contains("\"claudeUpdate\": " + (saved ? "true" : "false"), File.ReadAllText(home.Config.IdentityPath), StringComparison.Ordinal);
        Assert.Equal(saved, Config.SavedSettings.Apply(home.Config).ClaudeUpdateEnabled);
    }

    [Theory]
    [InlineData("config", "claude-update", "bogus")]
    [InlineData("config", "claude-update", "ON")]
    [InlineData("config", "claude-update")]
    [InlineData("config", "claude-update", "on", "extra")]
    public async Task Config_claude_update_with_anything_else_exits_2_with_the_usage_and_saves_nothing(params string[] args)
    {
        var (code, output, error) = await Cli_(args);
        Assert.Equal(2, code);
        Assert.Contains("usage: cm-agent config [auto-update off|check|on] [claude-update on|off]", error, StringComparison.Ordinal);
        Assert.Equal("", output);
        Assert.Null(Identity.Peek(home.Config)?.ClaudeUpdate);
    }

    [Fact]
    public async Task Saving_claude_update_leaves_the_auto_update_setting_alone()
    {
        await Cli_("config", "auto-update", "check");
        await Cli_("config", "claude-update", "on");
        var saved = Identity.Peek(home.Config)!;
        Assert.Equal(("check", true), (saved.AutoUpdate, saved.ClaudeUpdate));
        Assert.Contains("auto-update: check", (await Cli_("config")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_usage_text_lists_the_new_verb_and_setting()
    {
        var usage = (await Cli_()).Out;
        Assert.Contains("cm-agent claude-update cancel", usage, StringComparison.Ordinal);
        Assert.Contains("[claude-update on|off]", usage, StringComparison.Ordinal);
    }

    // ---- status ------------------------------------------------------------------------------------------------

    private async Task<string[]> StatusLines() => (await Cli_("status")).Out.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task Status_by_default_shows_off_on_both_sides_and_no_further_claude_lines()
    {
        var lines = await StatusLines();
        var at = Array.IndexOf(lines, "claude-update: off (this machine: off, workspace: off)");
        Assert.True(at >= 0, string.Join("|", lines));
        Assert.StartsWith("version: ", lines[at + 1], StringComparison.Ordinal); // nothing between it and the version
        Assert.DoesNotContain(lines, l => l.Contains("Claude Code", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Status_with_both_sides_off_hides_an_old_result()
    {
        State(new ClaudeUpdateState(CheckedAt: Iso(Now), Result: ClaudeCodes.Updated, Detail: "x"));
        Assert.DoesNotContain(await StatusLines(), l => l.Contains("last Claude Code update", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, null, "claude-update: off (this machine: on, workspace: off)")]
    [InlineData(false, "true", "claude-update: off (this machine: off, workspace: on)")]
    [InlineData(true, "false", "claude-update: off (this machine: on, workspace: off)")]
    [InlineData(true, "true", "claude-update: on (this machine: on, workspace: on)")]
    public async Task Status_shows_the_effective_setting_and_both_sides(bool machine, string? workspace, string expected)
    {
        if (machine) await Cli_("config", "claude-update", "on");
        if (workspace is not null) Workspace(workspace);
        Assert.Contains(expected, await StatusLines());
    }

    [Fact]
    public async Task Status_during_a_countdown_shows_when_the_update_starts_and_how_to_cancel_it()
    {
        await Cli_("config", "claude-update", "on");
        Workspace("true");
        var until = Now.AddMinutes(4);
        State(new ClaudeUpdateState(CountdownUntil: Iso(until)));
        var lines = await StatusLines();
        Assert.Contains($"Claude Code will be updated at {Local(until, "HH:mm")} (cancel: cm-agent claude-update cancel)", lines);

        State(new ClaudeUpdateState(CountdownUntil: Iso(Now.AddMinutes(-1))));
        Assert.DoesNotContain(await StatusLines(), l => l.Contains("will be updated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Status_after_an_update_shows_its_result_and_when_in_day_month_year_form()
    {
        await Cli_("config", "claude-update", "on");
        Workspace("true");
        State(new ClaudeUpdateState(CheckedAt: Iso(Now), Result: ClaudeCodes.Updated, Detail: "Claude Code 2.1.285 -> 2.1.291"));
        Assert.Contains($"last Claude Code update: updated (Claude Code 2.1.285 -> 2.1.291) {Local(Now, "dd/MM/yyyy HH:mm")}", await StatusLines());
    }

    [Fact]
    public async Task Status_shows_a_question_mark_when_the_time_of_the_last_attempt_is_unknown()
    {
        await Cli_("config", "claude-update", "on");
        State(new ClaudeUpdateState(Result: ClaudeCodes.Failed, Detail: "boom"));
        Assert.Contains("last Claude Code update: failed (boom) ?", await StatusLines());
    }

    // ---- old files ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_agent_json_from_before_the_setting_loads_and_means_off()
    {
        // the machine key is made up here, not written as a literal (a key-shaped string trips the secret scan)
        var machineKey = new string('k', 32);
        File.WriteAllText(home.Config.IdentityPath, $$"""{"machineKey":"{{machineKey}}","server":"https://monitor.invalid","autoUpdate":"check"}""");
        var saved = Identity.Load(home.Config);
        Assert.Null(saved.ClaudeUpdate);
        Assert.Equal("check", saved.AutoUpdate);
        Assert.False(Config.SavedSettings.Apply(home.Config).ClaudeUpdateEnabled);
        Workspace("true");
        using var store = new LocalStore(home.Config.DatabasePath);
        Assert.False(ClaudePolicy.Allowed(home.Config, store));
        Assert.Contains("claude-update: off (this machine: off, workspace: on)", await StatusLines());
    }
}
