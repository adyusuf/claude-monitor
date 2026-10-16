using ClaudeMonitor.Agent.Auth;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

/// <summary>"cm-agent config role" and SavedSettings.SaveRole (ADR-0008 §3a): what a machine says it is, saved in agent.json.</summary>
public sealed class RoleConfigTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow);

    public void Dispose() => home.Dispose();

    private async Task<(int Code, string Out, string Err)> Cli_(params string[] args)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var code = await Cli.RunAsync(args, home.Config, new StringReader(""), stdout, stderr, clock);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private (string? Environment, string? Function) Saved() => (Identity.Peek(home.Config)?.ClaimedEnvironment, Identity.Peek(home.Config)?.ClaimedFunction);

    [Fact]
    public async Task Config_role_saves_both_halves_of_the_claim_in_the_identity_file_and_says_so()
    {
        var (code, output, error) = await Cli_("config", "role", "test", "application");

        Assert.Equal((0, ""), (code, error));
        Assert.StartsWith("role claim: test/application", output, StringComparison.Ordinal);
        Assert.Equal(("test", "application"), Saved());
    }

    [Fact]
    public async Task Config_role_none_none_clears_a_claim()
    {
        await Cli_("config", "role", "production", "database");
        Assert.Equal(("production", "database"), Saved());

        var (code, output, _) = await Cli_("config", "role", "none", "none");

        Assert.Equal(0, code);
        Assert.StartsWith("role claim: not set", output, StringComparison.Ordinal);
        Assert.Equal((null, null), Saved());
    }

    [Fact]
    public async Task A_claim_does_not_disturb_what_else_the_identity_holds_and_each_half_can_be_set_alone()
    {
        await Cli_("config", "auto-update", "check");

        await Cli_("config", "role", "none", "database");

        Assert.Equal((null, "database"), Saved());
        Assert.Equal("check", Identity.Peek(home.Config)!.AutoUpdate);
    }

    [Theory]
    [InlineData("prod", "x")]
    [InlineData("production", "x")]
    [InlineData("prod", "database")]
    [InlineData("Production", "Database")]
    public async Task An_unknown_environment_or_function_prints_what_is_accepted_exits_two_and_saves_nothing(string environment, string function)
    {
        await Cli_("config", "role", "test", "web");

        var (code, output, error) = await Cli_("config", "role", environment, function);

        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.Contains("role takes an environment (dev|test|stage|preprod|production|other|none)", error, StringComparison.Ordinal);
        Assert.Contains("function (development|application|web|database|ci|storage|other|none)", error, StringComparison.Ordinal);
        Assert.Equal(("test", "web"), Saved()); // the earlier claim is untouched
    }

    [Theory]
    [InlineData("config", "role")]
    [InlineData("config", "role", "test")]
    [InlineData("config", "role", "test", "web", "extra")]
    public async Task A_role_with_the_wrong_number_of_words_prints_the_usage_and_exits_two(params string[] args)
    {
        var (code, _, error) = await Cli_(args);

        Assert.Equal(2, code);
        Assert.Contains("role <environment|none> <function|none>", error, StringComparison.Ordinal);
        Assert.Equal((null, null), Saved());
    }

    [Fact]
    public void SaveRole_refuses_a_value_outside_the_closed_sets_and_writes_nothing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SavedSettings.SaveRole(home.Config, "prod", null));
        Assert.Throws<ArgumentOutOfRangeException>(() => SavedSettings.SaveRole(home.Config, null, "db"));
        Assert.Null(Identity.Peek(home.Config));

        SavedSettings.SaveRole(home.Config, MachineEnvironments.Preprod, MachineFunctions.Ci);
        Assert.Equal((MachineEnvironments.Preprod, MachineFunctions.Ci), Saved());
    }

    [Fact]
    public async Task The_usage_text_documents_the_role_command()
    {
        var (_, output, _) = await Cli_();

        Assert.Contains("cm-agent config role <environment|none> <function|none>", output, StringComparison.Ordinal);
    }
}
