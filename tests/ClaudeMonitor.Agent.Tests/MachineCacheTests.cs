using System.Globalization;
using System.Net;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Contracts;
using static ClaudeMonitor.Agent.Tests.MachineViewKit;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The daemon's copy of the workspace's machines for the session-start block (ADR-0008 §3b): when it is written, cleared and switched off.</summary>
public sealed class MachineCacheTests : IDisposable
{
    private const string Route = "GET /api/agent/machines";

    private readonly Guid self = Guid.NewGuid();
    private readonly Guid workspace = Guid.NewGuid();
    private readonly MachineView me;
    private readonly MachineView other = View("other-host", effectiveEnvironment: "production", effectiveFunction: "database");
    private readonly RemoteFixture fx;
    private readonly MachineCache cache;

    public MachineCacheTests()
    {
        fx = new RemoteFixture();
        me = View("my-host", self);
        new Identity(new string('k', 24), AgentId: self, WorkspaceId: workspace).Save(fx.Home.Config);
        WorkspaceSettings.Tag(fx.Store, workspace); // the switch counts only for the workspace it was read for
        fx.Store.Set(MachineMonitor.RemoteRunsKey, "true");
        fx.Fake.On(Route, HttpStatusCode.OK, new[] { me, other });
        cache = new MachineCache(fx.Home.Config, fx.Store, fx.Api, fx.Clock);
    }

    public void Dispose() => fx.Dispose();

    private string? Text => fx.Store.Get(AgentConfig.MachinesContextKey);

    private void KeepSomething()
    {
        fx.Store.SetMany([(AgentConfig.MachinesContextKey, "old block"), (AgentConfig.MachinesContextAtKey, "old time")]);
    }

    [Fact]
    public async Task A_refresh_with_remote_runs_on_writes_the_block_and_the_time_it_was_made()
    {
        await cache.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, fx.Fake.Count(Route));
        Assert.Contains("other-host", Text, StringComparison.Ordinal);
        Assert.Contains("role=production/database (live database server)", Text, StringComparison.Ordinal);
        Assert.Contains("my-host (agent " + self.ToString("D") + ") (this machine)", Text, StringComparison.Ordinal); // the identity's agent id is this machine
        Assert.Equal(fx.Clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), fx.Store.Get(AgentConfig.MachinesContextAtKey));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("")]
    public async Task While_remote_runs_are_not_on_the_block_is_removed_and_the_api_is_not_asked(string? remoteRuns)
    {
        KeepSomething();
        if (remoteRuns is null) fx.Store.Remove(MachineMonitor.RemoteRunsKey);
        else fx.Store.Set(MachineMonitor.RemoteRunsKey, remoteRuns);

        await cache.RefreshAsync(CancellationToken.None);

        Assert.Null(Text);
        Assert.Null(fx.Store.Get(AgentConfig.MachinesContextAtKey));
        Assert.Equal(0, fx.Fake.Count(Route));
    }

    [Fact]
    public async Task With_the_block_turned_off_on_this_machine_it_is_removed_and_the_api_is_not_asked()
    {
        var off = new MachineCache(fx.Home.Config with { MachinesContextEnabled = false }, fx.Store, fx.Api, fx.Clock);
        KeepSomething();

        await off.RefreshAsync(CancellationToken.None);

        Assert.Null(Text);
        Assert.Null(fx.Store.Get(AgentConfig.MachinesContextAtKey));
        Assert.Equal(0, fx.Fake.Count(Route));
    }

    [Fact]
    public async Task An_api_that_does_not_know_the_call_clears_the_block_and_is_not_asked_again()
    {
        fx.Fake.Routes.Remove(Route); // the fake answers 404 to a route it does not know
        KeepSomething();

        await cache.RefreshAsync(CancellationToken.None);

        Assert.Null(Text);
        Assert.Null(fx.Store.Get(AgentConfig.MachinesContextAtKey));
        Assert.Equal(1, fx.Fake.Count(Route));

        fx.Fake.On(Route, HttpStatusCode.OK, new[] { me, other }); // even if it learns the call, the cache stays off until a restart
        await cache.RefreshAsync(CancellationToken.None);
        await cache.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, fx.Fake.Count(Route));
        Assert.Null(Text);
    }

    [Fact]
    public async Task Another_api_failure_keeps_the_old_block_and_the_next_refresh_asks_again()
    {
        KeepSomething();
        fx.Fake.On(Route, HttpStatusCode.BadGateway, "{}");

        await Assert.ThrowsAsync<ApiException>(() => cache.RefreshAsync(CancellationToken.None));
        Assert.Equal("old block", Text);

        fx.Fake.On(Route, HttpStatusCode.OK, new[] { me, other });
        await cache.RefreshAsync(CancellationToken.None);
        Assert.Contains("other-host", Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_the_workspace_has_only_this_machine_the_block_is_removed()
    {
        await cache.RefreshAsync(CancellationToken.None);
        Assert.NotNull(Text);

        fx.Fake.On(Route, HttpStatusCode.OK, new[] { me });
        await cache.RefreshAsync(CancellationToken.None);

        Assert.Null(Text);
        Assert.Null(fx.Store.Get(AgentConfig.MachinesContextAtKey));
    }

    [Fact]
    public void Store_with_a_single_self_machine_or_an_empty_list_clears_the_keys_and_with_another_machine_writes_them()
    {
        KeepSomething();
        cache.Store([me]);
        Assert.Null(Text);
        Assert.Null(fx.Store.Get(AgentConfig.MachinesContextAtKey));

        cache.Store([me, other]);
        Assert.StartsWith(ClaudeMonitor.Agent.Capture.MachineContext.Rules, Text, StringComparison.Ordinal);

        cache.Store([]);
        Assert.Null(Text);
    }

    [Fact]
    public async Task The_block_is_cut_at_the_configured_cap()
    {
        using var small = new RemoteFixture(c => c with { MachinesContextMax = 300 });
        new Identity(new string('k', 24), AgentId: Guid.NewGuid(), WorkspaceId: workspace).Save(small.Home.Config);
        WorkspaceSettings.Tag(small.Store, workspace);
        small.Store.Set(MachineMonitor.RemoteRunsKey, "true");
        small.Fake.On(Route, HttpStatusCode.OK, Enumerable.Range(0, 20).Select(i => View($"host-{i:00}")).ToArray());

        await new MachineCache(small.Home.Config, small.Store, small.Api, small.Clock).RefreshAsync(CancellationToken.None);

        var text = small.Store.Get(AgentConfig.MachinesContextKey)!;
        Assert.Contains(" more: monitor_machines", text, StringComparison.Ordinal);
        Assert.DoesNotContain("host-19", text, StringComparison.Ordinal);
    }
}
