using System.Net;
using System.Text.Json;
using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Agent.Metrics;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

public sealed class MachineMonitorTests : IDisposable
{
    private readonly RemoteFixture fx = new();
    private readonly FakeSource source;
    private readonly MachineMonitor monitor;

    public MachineMonitorTests()
    {
        source = new FakeSource(fx.Clock);
        monitor = new MachineMonitor(fx.Home.Config, fx.Store, fx.Api, fx.Clock, source);
        fx.Fake.On("POST /api/agent/metrics", RemoteFixture.NoContent);
        fx.Fake.On("POST /api/agent/alerts", RemoteFixture.NoContent);
        fx.Fake.On("PUT /api/agent/profile", RemoteFixture.NoContent);
    }

    public void Dispose() => fx.Dispose();

    private sealed class FakeSource(ManualClock clock) : IMetricsSource
    {
        public double? Cpu { get; set; } = 10;
        public int Calls { get; private set; }

        public MetricSample? Sample(DateTimeOffset now)
        {
            Calls++;
            return Cpu is { } cpu ? new MetricSample(clock.GetUtcNow(), cpu, 100, 1000, []) : null;
        }
    }

    private static T Body<T>(string body) => JsonSerializer.Deserialize<T>(body, ApiClient.Json)!;

    private List<MetricsReport> Reports() =>
        fx.Fake.Seen.Where(s => s.Path == "/api/agent/metrics").Select(s => Body<MetricsReport>(s.Body)).ToList();

    private List<AlertReport[]> AlertPosts() =>
        fx.Fake.Seen.Where(s => s.Path == "/api/agent/alerts").Select(s => Body<AlertReport[]>(s.Body)).ToList();

    [Fact]
    public async Task A_sample_is_posted_and_an_alert_the_workspace_thresholds_open_goes_with_it()
    {
        fx.Store.Set(MachineMonitor.ThresholdsKey, "50,90,90,0");
        source.Cpu = 75;
        await monitor.SampleAsync(CancellationToken.None);

        var sample = Assert.Single(Assert.Single(Reports()).Samples);
        Assert.Equal((75d, 100L, 1000L), (sample.CpuPct, sample.MemUsedBytes, sample.MemTotalBytes));
        var alert = Assert.Single(Assert.Single(AlertPosts()));
        Assert.Equal((AlertKinds.Cpu, AlertStates.Open, 50d), (alert.Kind, alert.State, alert.ThresholdPct));

        source.Cpu = null; // nothing new to say: nothing more is posted
        await monitor.SampleAsync(CancellationToken.None);
        Assert.Single(Reports());
        Assert.Single(AlertPosts());
    }

    [Fact]
    public async Task Without_saved_thresholds_the_defaults_apply_and_a_short_breach_opens_nothing()
    {
        source.Cpu = 99;
        await monitor.SampleAsync(CancellationToken.None);
        Assert.Single(Reports());
        Assert.Empty(AlertPosts()); // 90 % for 300 s by default: one sample is not sustained
    }

    [Fact]
    public async Task Thresholds_that_cannot_be_read_are_ignored_and_new_ones_apply_at_the_next_sample()
    {
        fx.Store.Set(MachineMonitor.ThresholdsKey, "50,90,x,0");
        source.Cpu = 99;
        await monitor.SampleAsync(CancellationToken.None);
        fx.Store.Set(MachineMonitor.ThresholdsKey, "1,2,3");
        await monitor.SampleAsync(CancellationToken.None);
        Assert.Empty(AlertPosts());

        fx.Store.Set(MachineMonitor.ThresholdsKey, "50,90,90,0");
        await monitor.SampleAsync(CancellationToken.None);
        Assert.Equal(AlertStates.Open, Assert.Single(Assert.Single(AlertPosts())).State);
    }

    [Fact]
    public async Task A_failed_post_keeps_the_samples_for_the_next_pass_and_sends_them_in_order()
    {
        fx.Fake.On("POST /api/agent/metrics", HttpStatusCode.BadGateway, "{}");
        for (var i = 0; i < 3; i++)
        {
            source.Cpu = 10 + i;
            await Assert.ThrowsAsync<ApiException>(() => monitor.SampleAsync(CancellationToken.None));
            fx.Clock.Advance(TimeSpan.FromSeconds(60));
        }

        fx.Fake.On("POST /api/agent/metrics", RemoteFixture.NoContent);
        source.Cpu = 13;
        await monitor.SampleAsync(CancellationToken.None);

        Assert.Equal([10d, 11d, 12d, 13d], Reports().Last().Samples.Select(s => s.CpuPct));
        source.Cpu = null;
        await monitor.SampleAsync(CancellationToken.None);
        Assert.Equal(4, Reports().Count); // three failures and one success; nothing left to send
    }

    [Fact]
    public async Task The_samples_kept_while_the_api_is_away_are_bounded_to_the_latest_sixty()
    {
        fx.Fake.On("POST /api/agent/metrics", HttpStatusCode.BadGateway, "{}");
        for (var i = 0; i < 70; i++)
        {
            source.Cpu = i;
            await Assert.ThrowsAsync<ApiException>(() => monitor.SampleAsync(CancellationToken.None));
            fx.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        fx.Fake.On("POST /api/agent/metrics", RemoteFixture.NoContent);
        source.Cpu = null;
        await monitor.SampleAsync(CancellationToken.None);

        var sent = Reports().Last().Samples;
        Assert.Equal(MachineMonitor.KeepSamples, sent.Count);
        Assert.Equal(Enumerable.Range(10, 60).Select(i => (double)i), sent.Select(s => s.CpuPct));
    }

    [Fact]
    public async Task An_alert_the_api_did_not_take_is_kept_and_sent_once_with_the_next_pass()
    {
        fx.Store.Set(MachineMonitor.ThresholdsKey, "50,90,90,0");
        fx.Fake.On("POST /api/agent/alerts", HttpStatusCode.BadGateway, "{}");
        source.Cpu = 75;
        await Assert.ThrowsAsync<ApiException>(() => monitor.SampleAsync(CancellationToken.None));

        fx.Fake.On("POST /api/agent/alerts", RemoteFixture.NoContent);
        await monitor.SampleAsync(CancellationToken.None);

        var delivered = AlertPosts().Last();
        Assert.Equal(AlertStates.Open, Assert.Single(delivered).State); // the alert opened once, though two passes saw the breach
        Assert.Equal(2, Reports().Count(r => r.Samples.Count > 0 && r.Samples.Count <= 2));
    }

    [Fact]
    public async Task An_api_that_does_not_know_the_report_switches_every_report_off_until_a_restart()
    {
        fx.Fake.On("POST /api/agent/metrics", HttpStatusCode.NotFound, "{}");
        await monitor.SampleAsync(CancellationToken.None);
        var seen = fx.Fake.Seen.Count;
        var sampled = source.Calls;

        await monitor.SampleAsync(CancellationToken.None);
        await monitor.ProfileAsync(CancellationToken.None);
        Assert.Equal(seen, fx.Fake.Seen.Count);
        Assert.Equal(sampled, source.Calls);
    }

    [Fact]
    public async Task A_404_on_the_profile_switches_the_sample_reports_off_too()
    {
        fx.Fake.On("PUT /api/agent/profile", HttpStatusCode.NotFound, "{}");
        await monitor.ProfileAsync(CancellationToken.None);
        await monitor.SampleAsync(CancellationToken.None);
        Assert.Equal(0, source.Calls);
        Assert.Empty(Reports());
    }

    [Fact]
    public async Task The_profile_tells_the_api_the_saved_exec_level_the_account_and_the_concurrency()
    {
        if (ExecPolicyLoader.RunningAsRoot()) return; // as root the level reported is always off
        new Identity(new string('k', 24), ExecLevel: ExecLevels.Argv).Save(fx.Home.Config);

        await monitor.ProfileAsync(CancellationToken.None);

        var profile = Body<AgentProfile>(Assert.Single(fx.Fake.Seen, s => s.Path == "/api/agent/profile").Body);
        Assert.Equal((ExecLevels.Argv, false, fx.Home.Config.ExecMaxConcurrent, Environment.UserName),
            (profile.ExecLevel, profile.ServiceMode, profile.MaxConcurrent, profile.OsAccount));
        Assert.False(string.IsNullOrEmpty(profile.OsVersion));
    }

    [Fact]
    public async Task A_service_agent_reports_off_when_no_admin_owned_policy_exists_whatever_its_own_file_says()
    {
        using var service = new RemoteFixture(c => c with { ServiceMode = true, ExecConfigPath = Path.Combine(c.Home, "missing-exec.json") });
        service.Fake.On("PUT /api/agent/profile", RemoteFixture.NoContent);
        new Identity(new string('k', 24), ExecLevel: ExecLevels.Shell).Save(service.Home.Config);
        var serviceMonitor = new MachineMonitor(service.Home.Config, service.Store, service.Api, service.Clock, source);

        await serviceMonitor.ProfileAsync(CancellationToken.None);

        var profile = Body<AgentProfile>(Assert.Single(service.Fake.Seen).Body);
        Assert.Equal((ExecLevels.Off, true), (profile.ExecLevel, profile.ServiceMode));
        Assert.Equal(ExecPolicy.Off, serviceMonitor.Policy());
    }

    [Fact]
    public void Remember_keeps_the_workspace_switch_and_the_thresholds_the_settings_pass_learned()
    {
        MachineMonitor.Remember(fx.Store, new AgentSettings(true, 1000, Guid.NewGuid(), UpdateModes.Off, true, new AlertThresholds(80, 81, 82, 83)));
        Assert.Equal(("true", "80,81,82,83"), (fx.Store.Get(MachineMonitor.RemoteRunsKey), fx.Store.Get(MachineMonitor.ThresholdsKey)));

        MachineMonitor.Remember(fx.Store, new AgentSettings(true, 1000, Guid.NewGuid()));
        Assert.Equal(("false", "80,81,82,83"), (fx.Store.Get(MachineMonitor.RemoteRunsKey), fx.Store.Get(MachineMonitor.ThresholdsKey)));
    }
}
