using System.Text.Json;
using ClaudeMonitor.Agent.Daemon;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Agent.Metrics;
using ClaudeMonitor.Agent.Net;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The capability "script" (ADR-0008 §3e): reported only when the level is shell without a ceiling and a trusted interpreter exists.</summary>
public sealed class ScriptCapabilityTests : IDisposable
{
    private readonly SessionsDir files = new();
    private readonly RemoteFixture fx;
    private ExecPolicy policy = new(ExecLevels.Shell, [], []);
    private List<ToolInfo> tools = [new("sh", "/bin/sh", null, true)];

    public ScriptCapabilityTests()
    {
        fx = new RemoteFixture(c => c with { ClaudeConfigDir = files.ConfigDir });
        fx.Fake.On("PUT /api/agent/profile", RemoteFixture.NoContent);
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

    private async Task<IReadOnlyList<string>?> CapabilitiesAsync()
    {
        var monitor = new MachineMonitor(fx.Home.Config, fx.Store, fx.Api, fx.Clock, new NoMetrics(), () => policy, () => (null, tools));
        await monitor.ProfileAsync(CancellationToken.None);
        var body = fx.Fake.Seen.Last(s => s.Path == "/api/agent/profile").Body;
        return JsonSerializer.Deserialize<AgentProfile>(body, ApiClient.Json)!.Capabilities;
    }

    [Fact]
    public async Task Shell_level_without_a_ceiling_and_a_trusted_interpreter_reports_script()
    {
        Assert.Equal([AgentCapabilities.Script, AgentCapabilities.ScriptFiles], await CapabilitiesAsync()); // an agent that runs scripts also runs their files and stdin (ADR-0010 §2)
    }

    [Fact]
    public async Task The_argv_level_a_ceiling_or_no_trusted_interpreter_reports_no_capability()
    {
        policy = new ExecPolicy(ExecLevels.Argv, [], []);
        Assert.Empty((await CapabilitiesAsync())!);
    }

    [Fact]
    public async Task A_ceiling_turns_shell_into_argv_so_no_script()
    {
        policy = new ExecPolicy(ExecLevels.Shell, ["/usr/bin/true"], []);
        Assert.Empty((await CapabilitiesAsync())!);
    }

    [Fact]
    public async Task An_untrusted_interpreter_does_not_count()
    {
        tools = [new("sh", "/home/x/sh", null, false), new("git", "/usr/bin/git", null, true)];
        Assert.Empty((await CapabilitiesAsync())!);
    }
}
