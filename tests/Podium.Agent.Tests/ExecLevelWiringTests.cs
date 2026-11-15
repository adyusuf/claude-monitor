using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Podium.Agent.Auth;
using Podium.Agent.Config;
using Podium.Agent.Daemon;
using Podium.Agent.Exec;
using Podium.Agent.Metrics;
using Podium.Agent.Net;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>
/// F-327 (F-137 leftovers): the exec-level change reaches <see cref="ExecLevelChanges.Handle"/> through the settings pass, its report rides on the
/// profile, a policy file the service cannot write gives <c>needs_admin</c>, and a change whose save failed is not used up, so it is tried again.
/// </summary>
public sealed class ExecLevelWiringTests : ExecLevelAgentFixture
{
    private sealed class NoMetrics : IMetricsSource
    {
        public MetricSample? Sample(DateTimeOffset now) => null;
    }

    [Fact]
    public async Task The_settings_pass_hands_the_delivered_request_to_the_machine_and_the_profile_carries_its_report()
    {
        if (ExecPolicyLoader.RunningAsRoot()) return; // as root the level reported is always off
        SaveIdentity(Agent, ExecLevels.Argv);
        var fake = new FakeApi();
        using var http = ApiClient.CreateHttp("https://aipod.invalid", fake);
        using var api = new ApiClient(http, Credentials.For(Home.Config), Home.Config.ApiCallTimeout);
        fake.On("GET /api/agent/settings", HttpStatusCode.OK, new AgentSettings(true, 4096, Guid.NewGuid(), ExecLevelRequest: Bare(ExecLevels.Off, 3)));
        fake.On("PUT /api/agent/profile", RemoteFixture.NoContent);

        await new Relay(Home.Config, Store, api, Clock).SettingsAsync(CancellationToken.None);

        Assert.Equal(ExecLevels.Off, LevelOnDisk());
        Assert.Equal(new ExecLevelReport(3, ExecLevelStates.Applied), ExecLevelChanges.Reported(Store, Agent));

        await new MachineMonitor(Home.Config, Store, api, Clock, new NoMetrics()).ProfileAsync(CancellationToken.None);

        var profile = JsonSerializer.Deserialize<AgentProfile>(Assert.Single(fake.Seen, s => s.Path == "/api/agent/profile").Body, ApiClient.Json)!;
        Assert.Equal(new ExecLevelReport(3, ExecLevelStates.Applied), profile.ExecLevelChange);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void A_service_whose_policy_file_it_cannot_write_reports_needs_admin_and_the_file_keeps_its_level()
    {
        if (OperatingSystem.IsWindows() || ExecPolicyLoader.RunningAsRoot()) return; // root writes anywhere
        var folder = Path.Combine(Home.Dir, "admin");
        var service = Home.Config with { ServiceMode = true, ExecConfigPath = Path.Combine(folder, "exec.json") };
        ExecPolicyLoader.Save(service.ExecConfigPath, ExecPolicy.Off with { Level = ExecLevels.Shell });
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.True(Handle(Bare(ExecLevels.Argv, 1), service));

            Assert.Equal(ExecLevels.Shell, ExecPolicyLoader.ReadExisting(service.ExecConfigPath)!.Level);
            Assert.Equal(new ExecLevelReport(1, ExecLevelStates.NeedsAdmin), ExecLevelChanges.Reported(Store, Agent));
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void A_change_whose_save_fails_is_not_recorded_so_the_next_pass_applies_it()
    {
        SaveIdentity(Agent, ExecLevels.Argv);
        var blocker = Home.Config.IdentityPath + ".tmp";
        Directory.CreateDirectory(blocker); // agent.json cannot be written: its temp file's name is taken by a folder

        Assert.False(Handle(Bare(ExecLevels.Off, 5)));
        Assert.Null(ExecLevelChanges.Reported(Store, Agent));
        Assert.Equal(ExecLevels.Argv, LevelOnDisk());
        Assert.Contains("exec level change failed", File.ReadAllText(Home.Config.LogPath), StringComparison.Ordinal);

        Directory.Delete(blocker);
        Assert.True(Handle(Bare(ExecLevels.Off, 5)));
        Assert.Equal(ExecLevels.Off, LevelOnDisk());
        Assert.Equal(new ExecLevelReport(5, ExecLevelStates.Applied), ExecLevelChanges.Reported(Store, Agent));
    }
}
