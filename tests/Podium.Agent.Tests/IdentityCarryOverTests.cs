using Podium.Agent.Auth;
using Podium.Agent.Config;

namespace Podium.Agent.Tests;

/// <summary>F-326: the ADR-0016 carry-over of an earlier home's machine key and tokens, and the homes that never read an earlier one.</summary>
public sealed class IdentityCarryOverTests : IDisposable
{
    private static readonly string Key = new('c', 32);
    private readonly string legacy = Path.Combine(Path.GetTempPath(), "aipod-legacy-" + Guid.NewGuid().ToString("N"));
    private readonly TempHome home;

    public IdentityCarryOverTests()
    {
        Directory.CreateDirectory(legacy);
        home = new TempHome(c => c with { LegacyHomes = [legacy] });
    }

    public void Dispose()
    {
        home.Dispose();
        if (Directory.Exists(legacy)) Directory.Delete(legacy, recursive: true);
    }

    private string LegacyIdentity => Path.Combine(legacy, "agent.json");

    private void WriteLegacy(string machineKey)
    {
        File.WriteAllText(LegacyIdentity, $$"""{"machineKey":"{{machineKey}}","server":"https://aipod.invalid","agentId":"{{Guid.NewGuid()}}"}""");
        var old = Credentials.Legacy(home.Config, legacy);
        old.Write(Credentials.Access, "old-access");
        old.Write(Credentials.Refresh, "old-refresh");
    }

    [Fact]
    public void An_earlier_home_with_a_valid_key_is_carried_over_with_both_tokens_and_left_as_it_was()
    {
        WriteLegacy(Key);
        var before = File.ReadAllText(LegacyIdentity);

        var found = Identity.Find(home.Config);

        Assert.NotNull(found);
        Assert.Equal(Key, found.MachineKey);
        Assert.True(found.Connected);
        Assert.Equal(before, File.ReadAllText(home.Config.IdentityPath));
        var store = Credentials.For(home.Config);
        Assert.Equal(("old-access", "old-refresh"), (store.Read(Credentials.Access), store.Read(Credentials.Refresh)));
        Assert.Equal(before, File.ReadAllText(LegacyIdentity)); // the earlier home is not touched
        Assert.Equal("old-access", Credentials.Legacy(home.Config, legacy).Read(Credentials.Access));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(home.Config.IdentityPath));
        Assert.Contains("carried over", File.ReadAllText(home.Config.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void It_happens_once_a_token_already_here_is_never_overwritten_and_a_later_change_there_is_not_followed()
    {
        Credentials.For(home.Config).Write(Credentials.Access, "mine");
        WriteLegacy(Key);

        Assert.True(IdentityCarryOver.TryCarry(home.Config));
        Assert.Equal("mine", Credentials.For(home.Config).Read(Credentials.Access));
        Assert.Equal("old-refresh", Credentials.For(home.Config).Read(Credentials.Refresh));

        WriteLegacy(new string('d', 32));
        Assert.True(IdentityCarryOver.TryCarry(home.Config)); // this home has one now
        Assert.Equal(Key, Identity.Find(home.Config)!.MachineKey);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    public void An_earlier_home_without_a_valid_machine_key_carries_nothing(string machineKey)
    {
        WriteLegacy(machineKey);

        Assert.Null(Identity.Find(home.Config));

        Assert.False(File.Exists(home.Config.IdentityPath));
        Assert.Null(Credentials.For(home.Config).Read(Credentials.Access));
    }

    [Fact]
    public void An_earlier_agent_json_that_is_a_link_carries_nothing()
    {
        if (OperatingSystem.IsWindows()) return;
        var elsewhere = Path.Combine(legacy, "real.json");
        File.WriteAllText(elsewhere, $$"""{"machineKey":"{{Key}}"}""");
        File.CreateSymbolicLink(LegacyIdentity, elsewhere);

        Assert.False(IdentityCarryOver.TryCarry(home.Config));
        Assert.False(File.Exists(home.Config.IdentityPath));
    }

    [Fact]
    public void A_service_or_an_explicit_home_reads_no_earlier_home_and_a_default_home_reads_the_old_variable_first()
    {
        string? Env(string key, string? service, string? explicitHome) => key switch
        {
            "AP_SERVICE" => service,
            "AP_AGENT_HOME" => explicitHome,
            "CM_AGENT_HOME" => legacy,
            _ => null,
        };

        Assert.Empty(AgentConfig.FromEnvironment(k => Env(k, "1", null)).LegacyHomes);
        Assert.Empty(AgentConfig.FromEnvironment(k => Env(k, null, home.Dir)).LegacyHomes);
        var user = AgentConfig.FromEnvironment(k => Env(k, null, null)).LegacyHomes;
        Assert.Equal(legacy, user[0]);
        Assert.Equal(AgentConfig.LegacyHomesFor(AgentConfig.Os, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), user.Skip(1));
    }
}
