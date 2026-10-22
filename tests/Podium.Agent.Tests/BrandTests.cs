using Podium.Agent.Config;
using Podium.Agent.Exec;

namespace Podium.Agent.Tests;

/// <summary>The program is called aipod (ADR-0016): its name, the process check and the AP_ variables.</summary>
public sealed class BrandTests
{
    [Fact]
    public void The_program_is_called_aipod() => Assert.Equal("aipod", Brand.Name);

    [Theory]
    [InlineData("aipod", true)]
    [InlineData("aipod.exe", true)]
    [InlineData("aipodw", true)]
    [InlineData("AIPOD.EXE", true)]
    [InlineData("claude", false)]
    [InlineData("cm-agent", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_process_is_ours_by_its_name(string? name, bool ours) => Assert.Equal(ours, Brand.IsOurProcess(name));

    [Fact]
    public void The_ap_variables_are_read_and_others_are_not_renamed()
    {
        var env = new Dictionary<string, string?> { ["AP_SERVER"] = "https://new.invalid/", ["AP_STOP_WAIT"] = "7", ["CLAUDE_CONFIG_DIR"] = "/x" };
        var config = AgentConfig.FromEnvironment(k => env.GetValueOrDefault(k));

        Assert.Equal("https://new.invalid", config.ServerOverride);
        Assert.Equal(TimeSpan.FromSeconds(7), config.StopWait);
        Assert.True(config.StopWaitFromEnvironment);
    }

    [Fact]
    public void An_empty_variable_is_not_set()
    {
        var env = new Dictionary<string, string?> { ["AP_SERVER"] = "", ["AP_STOP_WAIT"] = "" };
        var config = AgentConfig.FromEnvironment(k => env.GetValueOrDefault(k));

        Assert.Null(config.ServerOverride);
        Assert.False(config.StopWaitFromEnvironment);
    }

    [Fact]
    public void No_run_may_set_the_agents_variables() =>
        Assert.All(new[] { "AP_", "AIPOD_" }, p => Assert.Contains(p, RunEnvironment.ForbiddenPrefixes));
}
