using System.Text.Json;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Agent.Metrics;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The services and the project facts in the profile (ADR-0008 §3a′): services only after the owner opted in, facts of listed folders, re-read on a timer.</summary>
public sealed class MachineFactsProfileTests : IDisposable
{
    private const string Put = "PUT /api/agent/profile";
    private readonly SessionsDir files = new();
    private readonly RemoteFixture fx;
    private readonly MachineMonitor monitor;
    private readonly List<ServiceInfo> services = [new(5432, "tcp", "postgres")];
    private List<ProjectFact> projects = [new("git:host.example/team/shop", "shop", "ab12", "main", "0123abcd", null, ProjectSources.Listed)];
    private int serviceReads;
    private int projectReads;

    public MachineFactsProfileTests()
    {
        fx = new RemoteFixture(c => c with { ClaudeConfigDir = files.ConfigDir });
        fx.Fake.On(Put, RemoteFixture.NoContent);
        monitor = new MachineMonitor(fx.Home.Config, fx.Store, fx.Api, fx.Clock, new NoMetrics(), () => new ExecPolicy(ExecLevels.Argv, [], []),
            () => (null, []), () => { serviceReads++; return services; }, () => { projectReads++; return projects; });
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
    public async Task Without_the_owners_opt_in_no_service_is_read_or_sent_but_the_project_facts_are()
    {
        await monitor.ProfileAsync(CancellationToken.None);

        var profile = Assert.Single(Sent());
        Assert.Null(profile.Services);
        Assert.Equal(0, serviceReads); // the listening ports are not even read while the switch is off
        Assert.Equal("shop", Assert.Single(profile.Projects!).Name);
    }

    [Fact]
    public async Task After_the_opt_in_the_services_are_sent_and_turning_it_off_stops_them_at_once()
    {
        fx.Store.Set(MachineMonitor.ShareServicesKey, "true");
        await monitor.ProfileAsync(CancellationToken.None);
        Assert.Equal(5432, Assert.Single(Sent()[0].Services!).Port);

        fx.Store.Set(MachineMonitor.ShareServicesKey, "false"); // no waiting for the interval
        await monitor.ProfileAsync(CancellationToken.None);
        Assert.Null(Sent()[^1].Services);
    }

    [Fact]
    public async Task The_facts_are_read_once_per_interval_and_again_after_it()
    {
        await monitor.ProfileAsync(CancellationToken.None);
        fx.Clock.Advance(fx.Home.Config.ProjectFactsEvery - TimeSpan.FromSeconds(1));
        await monitor.ProfileAsync(CancellationToken.None);
        Assert.Equal(1, projectReads);

        projects = [.. projects, new ProjectFact("git:host.example/team/blog", "blog", null, "main", null, null, ProjectSources.Listed)];
        fx.Clock.Advance(TimeSpan.FromSeconds(2));
        await monitor.ProfileAsync(CancellationToken.None);

        Assert.Equal(2, projectReads);
        Assert.Equal(2, Sent()[^1].Projects!.Count);
    }

    [Fact]
    public async Task No_listed_folder_means_the_profile_carries_no_project_list()
    {
        projects = [];
        await monitor.ProfileAsync(CancellationToken.None);
        Assert.Null(Assert.Single(Sent()).Projects);
    }

    [Fact]
    public void The_interval_is_read_from_the_environment_between_thirty_seconds_and_an_hour()
    {
        AgentConfig With(string? value) => AgentConfig.FromEnvironment(k => k == "CM_PROJECT_FACTS_EVERY" ? value : null);
        Assert.Equal(TimeSpan.FromMinutes(5), With(null).ProjectFactsEvery);
        Assert.Equal(TimeSpan.FromSeconds(60), With("60").ProjectFactsEvery);
        Assert.Equal(TimeSpan.FromSeconds(30), With("5").ProjectFactsEvery); // out of range: clamped to the nearest bound
        Assert.Equal(TimeSpan.FromHours(1), With("99999").ProjectFactsEvery);
        Assert.Equal(TimeSpan.FromMinutes(5), With("soon").ProjectFactsEvery); // not a number: the default
    }
}
