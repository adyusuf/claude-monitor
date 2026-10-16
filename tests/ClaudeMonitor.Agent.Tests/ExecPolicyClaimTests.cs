using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>The role claim a machine makes about itself (ADR-0008 §3a) as the exec policy loads, keeps and saves it.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class ExecPolicyClaimTests : IDisposable
{
    private readonly ExecFixture fx = new();
    private string PolicyPath => Path.Combine(fx.Dir, "exec.json");

    public void Dispose() => fx.Dispose();

    private TempHome Interactive() => new(c => c with { ServiceMode = false, ExecConfigPath = PolicyPath });

    [Fact]
    public void An_interactive_agent_passes_a_valid_claim_through_with_its_level()
    {
        using var home = Interactive();

        var policy = ExecPolicyLoader.Load(home.Config, ExecLevels.Argv, MachineEnvironments.Production, MachineFunctions.Database);

        Assert.Equal(new ExecPolicy(ExecLevels.Argv, [], [], MachineEnvironments.Production, MachineFunctions.Database), policy);
    }

    [Theory]
    [InlineData("prod", "db")]
    [InlineData("PRODUCTION", "Database")]
    [InlineData("", "")]
    public void An_unknown_value_is_dropped_so_it_is_no_claim_at_all(string environment, string function)
    {
        using var home = Interactive();

        var policy = ExecPolicyLoader.Load(home.Config, ExecLevels.Argv, environment, function);

        Assert.Null(policy.ClaimedEnvironment);
        Assert.Null(policy.ClaimedFunction);
        Assert.Equal(ExecLevels.Argv, policy.Level);
    }

    [Fact]
    public void Each_half_of_the_claim_is_judged_on_its_own()
    {
        using var home = Interactive();

        var policy = ExecPolicyLoader.Load(home.Config, ExecLevels.Off, "nonsense", MachineFunctions.Database);

        Assert.Equal((null, MachineFunctions.Database), (policy.ClaimedEnvironment, policy.ClaimedFunction));
    }

    [Fact]
    public void An_agent_whose_exec_is_off_or_unknown_still_makes_its_claim()
    {
        using var home = Interactive();

        var unknown = ExecPolicyLoader.Load(home.Config, "root", MachineEnvironments.Production, null);
        var missing = ExecPolicyLoader.Load(home.Config, null, null, MachineFunctions.Database);

        Assert.Equal((ExecLevels.Off, MachineEnvironments.Production, null), (unknown.Level, unknown.ClaimedEnvironment, unknown.ClaimedFunction));
        Assert.Equal((ExecLevels.Off, null, MachineFunctions.Database), (missing.Level, missing.ClaimedEnvironment, missing.ClaimedFunction));
    }

    [Fact]
    public void Without_a_claim_the_policy_is_what_it_was_before()
    {
        using var home = Interactive();

        Assert.Equal(new ExecPolicy(ExecLevels.Shell, [], []), ExecPolicyLoader.Load(home.Config, ExecLevels.Shell));
        Assert.Equal(ExecPolicy.Off, ExecPolicyLoader.Load(home.Config, null));
    }

    [Fact]
    public void Save_writes_the_claim_into_the_admin_file_under_the_names_environment_and_function()
    {
        ExecPolicyLoader.Save(PolicyPath, new ExecPolicy(ExecLevels.Argv, ["/usr/bin/true"], [fx.Root], MachineEnvironments.Production, MachineFunctions.Database));

        var file = JsonNode.Parse(File.ReadAllText(PolicyPath))!;
        Assert.Equal(MachineEnvironments.Production, (string?)file["environment"]);
        Assert.Equal(MachineFunctions.Database, (string?)file["function"]);
        Assert.Equal(ExecLevels.Argv, (string?)file["level"]);
    }

    [Fact]
    public void A_saved_policy_read_back_for_a_reinstall_keeps_its_ceiling_and_its_claim()
    {
        var saved = new ExecPolicy(ExecLevels.Argv, ["/usr/bin/true"], [fx.Root], MachineEnvironments.Production, MachineFunctions.Database);
        ExecPolicyLoader.Save(PolicyPath, saved);

        var read = ExecPolicyLoader.ReadExisting(PolicyPath);

        Assert.Equal(saved.Level, read!.Level);
        Assert.Equal(saved.AllowedExecutables, read.AllowedExecutables);
        Assert.Equal(saved.AllowedRoots, read.AllowedRoots);
        Assert.Equal((MachineEnvironments.Production, MachineFunctions.Database), (read.ClaimedEnvironment, read.ClaimedFunction));
    }

    [Fact]
    public void A_file_with_a_claim_written_by_hand_keeps_it_when_read_for_a_reinstall()
    {
        File.WriteAllText(PolicyPath, """{"level":"argv","allowedExecutables":[],"allowedRoots":[],"environment":"stage","function":"web"}""");

        var read = ExecPolicyLoader.ReadExisting(PolicyPath);

        Assert.Equal((MachineEnvironments.Stage, MachineFunctions.Web), (read!.ClaimedEnvironment, read.ClaimedFunction));
    }

    [Fact]
    public void A_policy_without_a_claim_saves_and_reads_back_without_one()
    {
        ExecPolicyLoader.Save(PolicyPath, new ExecPolicy(ExecLevels.Shell, [], []));

        var read = ExecPolicyLoader.ReadExisting(PolicyPath)!;

        Assert.Equal((ExecLevels.Shell, null, null), (read.Level, read.ClaimedEnvironment, read.ClaimedFunction));
    }
}
