using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Capture;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Install;
using ClaudeMonitor.Agent.Storage;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>`cm-agent install --stop-wait`, the saved value the hooks then use, and the queue summary of `cm-agent status`.</summary>
public sealed class StopWaitTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly TempHome home = new();
    private readonly string source;

    public StopWaitTests()
    {
        source = Path.Combine(home.Dir, "downloaded-cm-agent");
        File.WriteAllText(source, "binary");
    }

    public void Dispose() => home.Dispose();

    private (int Code, string Out, string Err) Install(params string[] extra) => Install(home.Config, extra);

    private (int Code, string Out, string Err) Install(AgentConfig config, params string[] extra)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = Cli.Install(["install", .. extra], config, stdout, stderr, source, (_, _) => 0);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private int HookTimeout(string hook) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(home.Config.PluginDir, "monitor-agent", "hooks", "hooks.json")))!["hooks"]![hook]![0]!["hooks"]![0]!["timeout"]!.GetValue<int>();

    private static AgentConfig FromEnvironment(string? stopWait, string home) =>
        AgentConfig.FromEnvironment(k => k switch { "CM_AGENT_HOME" => home, "CM_STOP_WAIT" => stopWait, _ => null });

    [Fact]
    public void Install_with_a_stop_wait_writes_the_hook_timeout_and_keeps_the_value()
    {
        var (code, output, _) = Install("--stop-wait", "120");
        Assert.Equal(0, code);
        Assert.Equal(135, HookTimeout("Stop")); // the wait + 15 s
        Assert.Equal(10, HookTimeout("UserPromptSubmit"));
        Assert.Equal(17, HookTimeout("PermissionRequest")); // untouched: the 2 s wait of the test home + 15
        Assert.Equal(120, Identity.Peek(home.Config)!.StopWaitSeconds);
        Assert.Contains("up to 120 s", output, StringComparison.Ordinal);

        Assert.Equal(0, Install().Code); // a later plain install keeps what was chosen
        Assert.Equal(135, HookTimeout("Stop"));
        Assert.Equal(120, Identity.Peek(home.Config)!.StopWaitSeconds);
    }

    [Fact]
    public void Install_without_a_stop_wait_keeps_the_default_of_zero_and_says_how_to_change_it()
    {
        var (code, output, _) = Install();
        Assert.Equal(0, code);
        Assert.Equal(15, HookTimeout("Stop"));
        Assert.Null(Identity.Peek(home.Config)?.StopWaitSeconds);
        Assert.Contains("--stop-wait", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stop_wait_of_zero_switches_it_off_again_and_the_limit_is_590()
    {
        Install("--stop-wait", "590");
        Assert.Equal(605, HookTimeout("Stop"));
        Install("--stop-wait", "0");
        Assert.Equal(15, HookTimeout("Stop"));
        Assert.Equal(0, Identity.Peek(home.Config)!.StopWaitSeconds);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("591")]
    [InlineData("1.5")]
    [InlineData("")]
    public void A_bad_stop_wait_is_refused_and_nothing_changes(string value)
    {
        var (code, _, error) = Install("--stop-wait", value);
        Assert.Equal(2, code);
        Assert.Contains("--stop-wait takes whole seconds from 0 to 590", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(home.Config.PluginDir, ".claude-plugin", "marketplace.json")));
        Assert.Null(Identity.Peek(home.Config));
    }

    [Fact]
    public void A_stop_wait_without_a_value_is_refused()
    {
        Assert.Equal(2, Install("--stop-wait").Code);
    }

    [Fact]
    public void The_saved_value_applies_unless_the_environment_sets_one()
    {
        Assert.Equal(TimeSpan.Zero, SavedSettings.Apply(home.Config).StopWait);
        Assert.Null(Identity.Peek(home.Config)); // reading settings never creates the identity file

        SavedSettings.SaveStopWait(home.Config, 45);
        Assert.Equal(TimeSpan.FromSeconds(45), SavedSettings.Apply(home.Config).StopWait);
        Assert.Equal(TimeSpan.FromSeconds(7), SavedSettings.Apply(FromEnvironment("7", home.Dir)).StopWait);
        Assert.Equal(TimeSpan.FromSeconds(45), SavedSettings.Apply(FromEnvironment(null, home.Dir)).StopWait);
        Assert.Equal(TimeSpan.FromSeconds(45), SavedSettings.Apply(FromEnvironment("", home.Dir)).StopWait);
        Assert.True(FromEnvironment("0", home.Dir).StopWaitFromEnvironment);
        Assert.Equal(TimeSpan.Zero, SavedSettings.Apply(FromEnvironment("0", home.Dir)).StopWait);
    }

    [Fact]
    public void An_unreadable_identity_file_leaves_the_default()
    {
        File.WriteAllText(home.Config.IdentityPath, "{ not json");
        Assert.Equal(TimeSpan.Zero, SavedSettings.Apply(home.Config).StopWait);
        Assert.Null(Identity.Peek(home.Config));
    }

    [Fact]
    public void A_saved_value_outside_the_limit_is_clamped()
    {
        (Identity.Load(home.Config) with { StopWaitSeconds = 9999 }).Save(home.Config);
        Assert.Equal(TimeSpan.FromSeconds(590), SavedSettings.Apply(home.Config).StopWait);
        (Identity.Load(home.Config) with { StopWaitSeconds = -5 }).Save(home.Config);
        Assert.Equal(TimeSpan.Zero, SavedSettings.Apply(home.Config).StopWait);
        Assert.Throws<ArgumentOutOfRangeException>(() => SavedSettings.SaveStopWait(home.Config, 591));
    }

    [Fact]
    public async Task A_finished_turn_waits_for_the_saved_time_while_the_web_can_be_heard_and_then_takes_the_prompt()
    {
        new Identity("machine-key-of-the-test", "https://monitor.invalid", Guid.NewGuid(), Guid.NewGuid()).Save(home.Config);
        var credentials = Credentials.For(home.Config);
        credentials.Write(Credentials.Access, "access-1");
        credentials.Write(Credentials.Refresh, "refresh-1");
        var config = SavedSettings.Apply(SavedSettings.SaveStopWait(home.Config, 20));
        using var store = new LocalStore(home.Config.DatabasePath);
        store.Set(Relay.LastContactKey, Start.ToString("O", CultureInfo.InvariantCulture));

        var idle = new VirtualClock(Start);
        Assert.Null(await new HookRunner(config, store, idle).RunAsync("Stop", """{"session_id":"s1"}""", CancellationToken.None));
        Assert.True(idle.Elapsed >= TimeSpan.FromSeconds(20), $"waited {idle.Elapsed}");

        // A prompt that arrives while it waits is delivered as the next turn (the prompt is already queued here).
        store.SaveCommand(new LocalCommand("c1", "s1", CommandKinds.Prompt, "Next: run the tests", Start.AddMinutes(30)));
        var late = new VirtualClock(Start);
        var output = JsonNode.Parse((await new HookRunner(config, store, late).RunAsync("Stop", """{"session_id":"s1"}""", CancellationToken.None))!)!;
        Assert.Equal("block", output["decision"]!.GetValue<string>());
        Assert.Equal("Next: run the tests", output["reason"]!.GetValue<string>());
        Assert.Equal(TimeSpan.Zero, late.Elapsed);
    }

    [Fact]
    public async Task A_prompt_that_arrives_while_the_finished_turn_waits_starts_the_next_turn()
    {
        new Identity("machine-key-of-the-test", "https://monitor.invalid", Guid.NewGuid(), Guid.NewGuid()).Save(home.Config);
        var credentials = Credentials.For(home.Config);
        credentials.Write(Credentials.Access, "access-1");
        credentials.Write(Credentials.Refresh, "refresh-1");
        var config = SavedSettings.Apply(SavedSettings.SaveStopWait(home.Config, 30));
        using var hookStore = new LocalStore(home.Config.DatabasePath);
        hookStore.Set(Relay.LastContactKey, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        // The hook is one process waiting; the daemon is another, writing the command into the same database later.
        var hook = Task.Run(() => new HookRunner(config, hookStore, TimeProvider.System).RunAsync("Stop", """{"session_id":"s1"}""", CancellationToken.None));
        await Task.Delay(300);
        Assert.False(hook.IsCompleted, "the turn must still be waiting: nothing has arrived");

        using (var daemonStore = new LocalStore(home.Config.DatabasePath))
        {
            daemonStore.SaveCommand(new LocalCommand("c1", "s1", CommandKinds.Prompt, "Arrived while waiting", DateTimeOffset.UtcNow.AddMinutes(30)));
        }

        var output = JsonNode.Parse((await hook.WaitAsync(TimeSpan.FromSeconds(10)))!)!;
        Assert.Equal("block", output["decision"]!.GetValue<string>());
        Assert.Equal("Arrived while waiting", output["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_finished_turn_does_not_wait_when_the_web_cannot_be_heard()
    {
        var config = SavedSettings.SaveStopWait(home.Config, 20); // never logged in
        using var store = new LocalStore(home.Config.DatabasePath);
        var at = new VirtualClock(Start);
        Assert.Null(await new HookRunner(config, store, at).RunAsync("Stop", """{"session_id":"s1"}""", CancellationToken.None));
        Assert.Equal(TimeSpan.Zero, at.Elapsed);
        Assert.Equal(0, at.Timers);
    }

    [Fact]
    public async Task Status_shows_the_wait_the_hooks_will_use()
    {
        async Task<string> Status()
        {
            var output = new StringWriter();
            await Cli.RunAsync(["status"], home.Config, new StringReader(""), output, new StringWriter(), new ManualClock(Start));
            return output.ToString();
        }

        Assert.Contains("stop wait: 0 s", await Status(), StringComparison.Ordinal);
        SavedSettings.SaveStopWait(home.Config, 30);
        Assert.Contains("stop wait: 30 s", await Status(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_counts_the_commands_still_waiting_and_names_the_soonest_expiry()
    {
        var clock = new ManualClock(Start);
        async Task<string> Status()
        {
            var output = new StringWriter();
            await Cli.StatusAsync(home.Config, output, clock);
            return output.ToString();
        }

        Assert.Contains("commands waiting: 0" + Environment.NewLine, await Status(), StringComparison.Ordinal);

        using (var store = new LocalStore(home.Config.DatabasePath))
        {
            store.SaveCommand(new LocalCommand("late", "s1", CommandKinds.Prompt, "a", Start.AddMinutes(30)));
            store.SaveCommand(new LocalCommand("soon", "s1", CommandKinds.Prompt, "b", Start.AddMinutes(12)));
            store.SaveCommand(new LocalCommand("gone", "s2", CommandKinds.Prompt, "c", Start.AddMinutes(-1)));
            store.SaveCommand(new LocalCommand("done", "s3", CommandKinds.Prompt, "d", Start.AddMinutes(5)));
            Assert.NotNull(store.TakeCommand("s3", CommandKinds.Prompt, Start));
        }

        var soonest = Start.AddMinutes(12).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        Assert.Contains($"commands waiting: 2 (oldest expires {soonest}){Environment.NewLine}", await Status(), StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromMinutes(13)); // the soon one lapses, the late one is left
        var later = Start.AddMinutes(30).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        Assert.Contains($"commands waiting: 1 (oldest expires {later})", await Status(), StringComparison.Ordinal);
    }
}
