using System.Net;
using System.Text.Json;
using Podium.Agent.Auth;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Exec;
using Podium.Agent.Metrics;
using Podium.Agent.Net;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The profile the daemon sends (ADR-0008): the role claim, Claude presence, the tool inventory and the capabilities, sent again only when they change.</summary>
public sealed class MachineProfileTests : IDisposable
{
    private const string Put = "PUT /api/agent/profile";

    private readonly SessionsDir files = new(); // an empty stand-in Claude config folder: the session counts never read the user's
    private readonly RemoteFixture fx;
    private readonly MachineMonitor monitor;
    private ExecPolicy policy = new(ExecLevels.Argv, [], [], MachineEnvironments.Production, MachineFunctions.Database);
    private ClaudePresence? claude;
    private List<ToolInfo> tools = [];
    private int probes;

    public MachineProfileTests()
    {
        claude = new ClaudePresence(ClaudeKinds.Native, "~/.local/share/claude/versions/2.1.300/claude", "2.1.300", 0, 0, default);
        tools = [new("sh", "/bin/sh", "bash 3.2", true), new("bash", "/opt/x/bash", null, false), new("python3", "/usr/bin/python3", "3.12", true), new("git", "/usr/bin/git", "2.40", true)];
        fx = new RemoteFixture(c => c with { ClaudeConfigDir = files.ConfigDir });
        fx.Fake.On(Put, RemoteFixture.NoContent);
        monitor = new MachineMonitor(fx.Home.Config, fx.Store, fx.Api, fx.Clock, new NoMetrics(), () => policy, () => { probes++; return (claude, tools); });
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

    private List<AgentProfile> Sent() => [.. fx.Fake.Seen.Where(s => s.Path == "/api/agent/profile")
        .Select(s => JsonSerializer.Deserialize<AgentProfile>(s.Body, ApiClient.Json)!)];

    [Fact]
    public async Task The_profile_carries_the_claim_claude_the_trusted_interpreters_the_tools_and_an_empty_capability_list()
    {
        await monitor.ProfileAsync(CancellationToken.None);

        var body = Assert.Single(fx.Fake.Seen, s => s.Path == "/api/agent/profile").Body;
        var profile = Assert.Single(Sent());
        Assert.Equal((MachineEnvironments.Production, MachineFunctions.Database), (profile.ClaimedEnvironment, profile.ClaimedFunction));
        Assert.Equal((ClaudeKinds.Native, "2.1.300", 0, 0), (profile.Claude!.Kind, profile.Claude.Version, profile.Claude.SessionsLive, profile.Claude.SessionsIdle));
        Assert.Equal(["sh", "python3"], profile.Interpreters!.Select(t => t.Name)); // bash is untrusted, git is no interpreter
        Assert.Equal(["sh", "bash", "python3", "git"], profile.Tools!.Select(t => t.Name));
        Assert.Contains("\"capabilities\":[]", body, StringComparison.Ordinal); // an empty list, not a missing one: it tells a new agent from an old one
    }

    [Fact]
    public async Task A_machine_that_claims_nothing_and_has_no_claude_sends_no_claim_and_no_presence()
    {
        policy = new ExecPolicy(ExecLevels.Argv, [], []);
        claude = null;
        tools = [];

        await monitor.ProfileAsync(CancellationToken.None);

        var profile = Assert.Single(Sent());
        Assert.Null(profile.ClaimedEnvironment);
        Assert.Null(profile.ClaimedFunction);
        Assert.Null(profile.Claude);
        Assert.Empty(profile.Tools!);
        Assert.Empty(profile.Interpreters!);
    }

    [Fact]
    public async Task A_second_call_with_unchanged_facts_sends_nothing()
    {
        await monitor.ProfileAsync(CancellationToken.None);
        fx.Clock.Advance(TimeSpan.FromMinutes(5)); // the presence time moves on, the facts do not

        await monitor.ProfileAsync(CancellationToken.None);

        Assert.Single(Sent());
        Assert.Equal(1, probes); // within the probe interval the cached inventory is reused
    }

    [Fact]
    public async Task A_changed_claim_is_sent_again()
    {
        await monitor.ProfileAsync(CancellationToken.None);

        policy = new ExecPolicy(ExecLevels.Argv, [], [], MachineEnvironments.Test, null);
        await monitor.ProfileAsync(CancellationToken.None);

        var sent = Sent();
        Assert.Equal(2, sent.Count);
        Assert.Equal((MachineEnvironments.Test, null), (sent[1].ClaimedEnvironment, sent[1].ClaimedFunction));
    }

    [Fact]
    public async Task A_new_session_is_sent_again_within_the_probe_interval_without_probing_the_machine_again()
    {
        await monitor.ProfileAsync(CancellationToken.None);

        files.Session(Environment.ProcessId, "idle", fx.Clock.GetUtcNow().AddMinutes(-30)); // this process stands in for a live session
        await monitor.ProfileAsync(CancellationToken.None);

        var sent = Sent();
        Assert.Equal(2, sent.Count);
        Assert.Equal((1, 1), (sent[1].Claude!.SessionsLive, sent[1].Claude!.SessionsIdle));
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task After_the_probe_interval_the_inventory_is_probed_again_and_a_changed_tool_list_is_sent()
    {
        await monitor.ProfileAsync(CancellationToken.None);

        tools = [.. tools, new ToolInfo("node", "/usr/bin/node", "22.1.0", true)];
        await monitor.ProfileAsync(CancellationToken.None);
        Assert.Single(Sent()); // not yet: the interval has not passed

        fx.Clock.Advance(fx.Home.Config.PresenceProbeEvery + TimeSpan.FromSeconds(1));
        await monitor.ProfileAsync(CancellationToken.None);

        Assert.Equal(2, probes);
        Assert.Contains(Sent()[1].Tools!, t => t.Name == "node");
    }

    [Fact]
    public async Task A_404_on_the_profile_switches_it_off_and_nothing_more_is_sent_or_probed()
    {
        fx.Fake.On(Put, HttpStatusCode.NotFound, "{}");
        await monitor.ProfileAsync(CancellationToken.None);

        fx.Fake.On(Put, RemoteFixture.NoContent);
        claude = claude! with { Version = "3.0.0" };
        fx.Clock.Advance(TimeSpan.FromDays(1));
        await monitor.ProfileAsync(CancellationToken.None);

        Assert.Equal(1, fx.Fake.Count(Put));
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task A_failed_send_is_retried_by_the_next_call_not_forgotten()
    {
        fx.Fake.On(Put, HttpStatusCode.BadGateway, "{}");
        await Assert.ThrowsAsync<ApiException>(() => monitor.ProfileAsync(CancellationToken.None));

        fx.Fake.On(Put, RemoteFixture.NoContent);
        await monitor.ProfileAsync(CancellationToken.None);

        Assert.Equal(2, fx.Fake.Count(Put));
        await monitor.ProfileAsync(CancellationToken.None);
        Assert.Equal(2, fx.Fake.Count(Put)); // and now it is the last one the API took
    }

    [Fact]
    public async Task Without_a_policy_source_the_claim_saved_by_config_role_is_what_the_profile_carries()
    {
        SavedSettings.SaveRole(fx.Home.Config, MachineEnvironments.Stage, MachineFunctions.Web);
        var plain = new MachineMonitor(fx.Home.Config, fx.Store, fx.Api, fx.Clock, new NoMetrics(), factsSource: () => (null, []));

        await plain.ProfileAsync(CancellationToken.None);

        var profile = Assert.Single(Sent());
        Assert.Equal((MachineEnvironments.Stage, MachineFunctions.Web), (profile.ClaimedEnvironment, profile.ClaimedFunction));
        Assert.Equal(MachineEnvironments.Stage, Identity.Peek(fx.Home.Config)!.ClaimedEnvironment);
    }
}
