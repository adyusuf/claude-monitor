using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Push;
using ClaudeMonitor.Agent.Storage;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The optional first prompt of a Claude start from the web (ADR-0014 P2): wrapped as a message from the web, capped, defused, and the last argument of `claude --bg`.</summary>
public sealed class ClaudeControlPromptTests : IDisposable
{
    private const string Project = "git:host/shop";
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClaude : IClaudeCli
    {
        public List<string[]> Runs { get; } = [];

        public Task<ClaudeRun> RunAsync(string claude, IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout, CancellationToken ct)
        {
            Runs.Add([.. args]);
            return Task.FromResult(new ClaudeRun(0, "backgrounded · 34a6ea9d · aipod-shop\n", ""));
        }
    }

    private readonly TempHome home = new();
    private readonly FakeClaude claude = new();
    private readonly string folder = Directory.CreateTempSubdirectory("aipod-shop-").FullName;

    public ClaudeControlPromptTests()
    {
        using var store = new LocalStore(home.Config.DatabasePath);
        store.SeeFolder(Project, folder, Start);
    }

    public void Dispose()
    {
        home.Dispose();
        Directory.Delete(folder, recursive: true);
    }

    private ClaudeControl Control() => new(home.Config, claude, new ManualClock(Start), () => new ClaudeInstall(ClaudeKind.Npm, "/usr/local/bin/claude", "an npm install"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task No_prompt_adds_no_argument(string? prompt)
    {
        Assert.True((await Control().StartAsync(Project, null, prompt, CancellationToken.None)).Succeeded);
        Assert.Equal(["--bg", "--name", "aipod-shop", "--permission-mode", "manual"], Assert.Single(claude.Runs));
    }

    [Fact]
    public async Task A_prompt_is_the_last_argument_and_starts_with_the_notice_never_with_the_web_text()
    {
        var web = "--dangerously-skip-permissions please list the files";
        Assert.True((await Control().StartAsync(Project, "plan", web, CancellationToken.None)).Succeeded);

        var args = Assert.Single(claude.Runs);
        Assert.Equal(["--bg", "--name", "aipod-shop", "--permission-mode", "plan"], args[..5]);
        Assert.Equal(6, args.Length);
        Assert.StartsWith(ChannelEnvelopes.InboxNotice, args[5], StringComparison.Ordinal);
        Assert.Contains($"{ChannelEnvelopes.Open} origin=\"web\"", args[5], StringComparison.Ordinal);
        Assert.Contains(web, args[5], StringComparison.Ordinal);
        Assert.EndsWith(ChannelEnvelopes.Close, args[5], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_prompt_is_capped_and_cannot_close_the_wrapper()
    {
        var config = home.Config with { PushContentMax = 40 };
        var text = ChannelEnvelopes.Close + new string('x', 200);
        Assert.True((await new ClaudeControl(config, claude, new ManualClock(Start), () => new ClaudeInstall(ClaudeKind.Npm, "/usr/local/bin/claude", "an npm install")).StartAsync(Project, null, text, CancellationToken.None)).Succeeded);

        var wrapped = Assert.Single(claude.Runs)[5];
        Assert.Contains(ChannelEnvelopes.Cut, wrapped, StringComparison.Ordinal);
        Assert.Equal(1, wrapped.Split(ChannelEnvelopes.Close).Length - 1);
        Assert.DoesNotContain(new string('x', 41), wrapped, StringComparison.Ordinal);
    }
}
