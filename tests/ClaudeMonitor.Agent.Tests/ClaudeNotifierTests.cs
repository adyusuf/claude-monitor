using System.Collections.Concurrent;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Update;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The desktop notice before a Claude Code update. Only macOS has one; the positive path is asserted there and never faked elsewhere.</summary>
public sealed class ClaudeNotifierTests
{
    private const string Text = "Claude Code will be updated in 5 min. Cancel with: cm-agent claude-update cancel";

    private sealed class Osascript(int exit = 0) : IProcessRunner
    {
        public ConcurrentQueue<(string File, string[] Args)> Calls { get; } = new();

        public (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args, TimeSpan timeout)
        {
            Calls.Enqueue((file, [.. args]));
            return (exit, "");
        }
    }

    [Fact]
    public void Where_there_is_no_notifier_it_says_no_and_runs_nothing()
    {
        if (OperatingSystem.IsMacOS()) return; // macOS has one: asserted below. Windows has none built (a toast was not built or tested).
        var runner = new Osascript();
        Assert.False(new SystemNotifier(runner, TimeSpan.FromSeconds(5)).Notify(Text));
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData("He said \"hi\" \\ there")]
    [InlineData("line one\nline two")]
    [InlineData("\" & do shell script \"x\"")]
    [InlineData("")]
    public void Where_there_is_no_notifier_no_message_whatever_it_holds_reaches_any_program(string message)
    {
        if (OperatingSystem.IsMacOS()) return; // the macOS cases below assert what reaches osascript
        var runner = new Osascript();
        Assert.False(new SystemNotifier(runner, TimeSpan.FromSeconds(5)).Notify(message));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void On_macOS_it_shows_a_notification_through_osascript_and_says_yes_when_that_worked()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var runner = new Osascript();
        Assert.True(new SystemNotifier(runner, TimeSpan.FromSeconds(5)).Notify(Text));
        var (file, args) = Assert.Single(runner.Calls);
        Assert.Equal("/usr/bin/osascript", file);
        Assert.Equal("-e", args[0]);
        Assert.Equal(2, args.Length);
        Assert.StartsWith("display notification \"", args[1], StringComparison.Ordinal);
        Assert.Contains("cm-agent claude-update cancel", args[1], StringComparison.Ordinal);
        Assert.Contains("in 5 min", args[1], StringComparison.Ordinal);
        Assert.EndsWith("\" with title \"Claude Monitor\"", args[1], StringComparison.Ordinal);
    }

    [Fact]
    public void On_macOS_a_failing_osascript_is_no_notification()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.False(new SystemNotifier(new Osascript(exit: 1), TimeSpan.FromSeconds(5)).Notify(Text));
    }

    [Theory]
    [InlineData("He said \"hi\" \\ there")]
    [InlineData("line one\nline two\r\nline three")]
    [InlineData("\" & do shell script \"x\"")]
    [InlineData("\\\" & do shell script \"touch /tmp/x\" & \\\"")]
    [InlineData("it's $(rm -rf ~) `id` ; | > < * ? { } [ ] ' \u2028 \u00e7 \u202e")]
    public void On_macOS_the_message_is_reduced_to_plain_characters_before_it_reaches_AppleScript(string hostile)
    {
        if (!OperatingSystem.IsMacOS()) return; // elsewhere nothing is ever built or run: see the first test
        var runner = new Osascript();
        new SystemNotifier(runner, TimeSpan.FromSeconds(5)).Notify(hostile);
        var script = Assert.Single(runner.Calls).Args[1];

        Assert.Equal(4, script.Count(c => c == '"')); // only the two pairs this agent wrote itself
        Assert.DoesNotContain('\\', script);
        Assert.DoesNotContain('\n', script);
        Assert.DoesNotContain('\r', script);
        var shown = script["display notification \"".Length..script.IndexOf("\" with title", StringComparison.Ordinal)];
        Assert.All(shown, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or ',' or ':' or '-' or '(' or ')' or '`', $"'{c}' reached AppleScript"));
    }

    [Fact]
    public void On_macOS_the_agents_own_notice_survives_the_reduction_unchanged()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var runner = new Osascript();
        new SystemNotifier(runner, TimeSpan.FromSeconds(5)).Notify(Text);
        Assert.Equal($"display notification \"{Text}\" with title \"Claude Monitor\"", Assert.Single(runner.Calls).Args[1]);
    }
}
