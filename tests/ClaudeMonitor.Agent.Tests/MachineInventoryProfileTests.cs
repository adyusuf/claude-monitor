using System.Text.Json;
using ClaudeMonitor.Agent.ClaudeUpdate;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Agent.Metrics;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The Claude inventory in the profile (ADR-0011 §7a, step E): sent when a reader is wired, read once per interval, and a change in content, not the time of reading, makes a new profile.</summary>
public sealed class MachineInventoryProfileTests : IDisposable
{
    private const string Put = "PUT /api/agent/profile";
    private readonly SessionsDir files = new();
    private readonly RemoteFixture fx;
    private readonly FakeCli cli = new();

    public MachineInventoryProfileTests()
    {
        fx = new RemoteFixture(c => c with { ClaudeConfigDir = files.ConfigDir });
        fx.Fake.On(Put, RemoteFixture.NoContent);
        fx.Store.Set(MachineMonitor.ShareInventoryKey, "true"); // the owner switched it on (the settings pass remembers it)
    }

    public void Dispose()
    {
        fx.Dispose();
        files.Dispose();
    }

    private sealed class NoMetrics : IMetricsSource
    {
        public MetricSample? Sample(DateTimeOffset now) => null;
    }

    private sealed class FakeCli : IClaudeCli
    {
        public string Output { get; set; } = "[{\"id\":\"a@m\",\"version\":\"1\",\"enabled\":true}]";

        public Task<ClaudeRun> RunAsync(string claude, IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(new ClaudeRun(0, Output, ""));
    }

    private MachineMonitor Monitor(bool withReader = true) => new(fx.Home.Config, fx.Store, fx.Api, fx.Clock, new NoMetrics(), () => new ExecPolicy(ExecLevels.Argv, [], []), () => (null, []),
        inventoryReader: withReader ? new ClaudeInventoryReader(fx.Home.Config with { ClaudeConfigDir = files.ConfigDir }, cli, () => new ClaudeInstall(ClaudeKind.Npm, "/usr/local/bin/claude", ""), _ => true) : null);

    private List<AgentProfile> Sent() => [.. fx.Fake.Seen.Where(s => s.Path == "/api/agent/profile").Select(s => JsonSerializer.Deserialize<AgentProfile>(s.Body, ApiClient.Json)!)];

    [Fact]
    public async Task The_profile_carries_the_inventory_when_a_reader_is_wired_and_none_when_it_is_not()
    {
        await Monitor().ProfileAsync(CancellationToken.None);
        Assert.Equal("a@m", Assert.Single(Sent()[0].Inventory!.Plugins!).Id);

        await Monitor(withReader: false).ProfileAsync(CancellationToken.None);
        Assert.Null(Sent()[^1].Inventory);
    }

    [Fact]
    public async Task The_same_content_read_again_is_not_sent_again_but_a_change_is()
    {
        var monitor = Monitor();
        await monitor.ProfileAsync(CancellationToken.None);
        fx.Clock.Advance(fx.Home.Config.ClaudeInventoryEvery + TimeSpan.FromSeconds(1));
        await monitor.ProfileAsync(CancellationToken.None);
        Assert.Single(Sent());

        cli.Output = "[{\"id\":\"b@m\",\"version\":\"1\",\"enabled\":true}]";
        fx.Clock.Advance(ClaudeInventoryReader.PluginsEvery + TimeSpan.FromSeconds(1));
        await monitor.ProfileAsync(CancellationToken.None);

        Assert.Equal(2, Sent().Count);
        Assert.Equal("b@m", Assert.Single(Sent()[1].Inventory!.Plugins!).Id);
    }

    [Fact]
    public async Task While_the_owners_switch_is_off_nothing_is_read_or_sent_and_turning_it_off_stops_it_at_once()
    {
        var monitor = Monitor();
        fx.Store.Set(MachineMonitor.ShareInventoryKey, "false");
        await monitor.ProfileAsync(CancellationToken.None);
        Assert.Null(Assert.Single(Sent()).Inventory);

        fx.Store.Set(MachineMonitor.ShareInventoryKey, "true");
        await monitor.ProfileAsync(CancellationToken.None);
        Assert.NotNull(Sent()[^1].Inventory);

        fx.Store.Set(MachineMonitor.ShareInventoryKey, "false"); // no waiting for the interval
        await monitor.ProfileAsync(CancellationToken.None);
        Assert.Null(Sent()[^1].Inventory);
    }
}
