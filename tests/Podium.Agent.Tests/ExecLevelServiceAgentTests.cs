using Podium.Agent.Config;
using Podium.Agent.Exec;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>A service's level lives in an admin-owned file it cannot raise (ADR-0021): it lowers the file only when it can write it, otherwise the admin has to.</summary>
public sealed class ExecLevelServiceAgentTests : ExecLevelAgentFixture
{
    private AgentConfig ServiceConfig => Home.Config with { ServiceMode = true, ExecConfigPath = Path.Combine(Home.Dir, "exec.json") };

    private void WritePolicy(string level) => ExecPolicyLoader.Save(ServiceConfig.ExecConfigPath, ExecPolicy.Off with { Level = level });

    private string PolicyLevel() => ExecPolicyLoader.ReadExisting(ServiceConfig.ExecConfigPath)!.Level;

    [Fact]
    public void A_service_with_no_admin_policy_file_reports_needs_admin_for_a_lowering_and_writes_none()
    {
        Handle(Bare(ExecLevels.Off, 1), ServiceConfig);

        Assert.Equal(new ExecLevelReport(1, ExecLevelStates.NeedsAdmin), ExecLevelChanges.Reported(Store, Agent));
        Assert.False(File.Exists(ServiceConfig.ExecConfigPath));
    }

    [Fact]
    public void A_service_lowers_the_admin_file_when_it_can_write_it_and_reports_applied()
    {
        WritePolicy(ExecLevels.Shell);

        Handle(Bare(ExecLevels.Argv, 1), ServiceConfig);

        Assert.Equal(ExecLevels.Argv, PolicyLevel());
        Assert.Equal(new ExecLevelReport(1, ExecLevelStates.Applied), ExecLevelChanges.Reported(Store, Agent));
    }

    [Fact]
    public void A_service_asked_for_the_level_its_file_already_holds_reports_applied_and_leaves_the_file()
    {
        WritePolicy(ExecLevels.Argv);

        Handle(Bare(ExecLevels.Argv, 1), ServiceConfig);

        Assert.Equal(ExecLevels.Argv, PolicyLevel());
        Assert.Equal(ExecLevelStates.Applied, ExecLevelChanges.Reported(Store, Agent)!.State);
    }

    [Fact]
    public void A_raise_on_a_service_is_not_believed_when_the_pinned_keys_are_not_in_an_admin_owned_file_and_the_policy_file_stays()
    {
        WritePolicy(ExecLevels.Argv);
        Pin();

        Handle(OwnerSigned(ExecLevels.Shell, 1), ServiceConfig);

        Assert.Equal(ExecLevels.Argv, PolicyLevel());
        Assert.Equal(new ExecLevelReport(1, ExecLevelStates.NoPinnedKey), ExecLevelChanges.Reported(Store, Agent));
    }
}
