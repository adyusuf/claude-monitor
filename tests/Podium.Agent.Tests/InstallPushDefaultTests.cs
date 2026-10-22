using Podium.Agent.Auth;
using Podium.Agent.Config;

namespace Podium.Agent.Tests;

/// <summary>The first `aipod install` turns the inbox push on (it needs no start-up flag); a choice made afterwards (off, channel) is never overridden by a later install.</summary>
public sealed class InstallPushDefaultTests : IDisposable
{
    private readonly TempHome home = new();
    private readonly string source;

    public InstallPushDefaultTests()
    {
        source = Path.Combine(home.Dir, "downloaded-aipod");
        File.WriteAllText(source, "binary");
    }

    public void Dispose() => home.Dispose();

    private (int Code, string Out) Install(AgentConfig config, params string[] extra)
    {
        var stdout = new StringWriter();
        var code = Cli.Install(["install", .. extra], config, stdout, new StringWriter(), source, (_, _) => 0);
        return (code, stdout.ToString());
    }

    [Fact]
    public void The_first_install_saves_the_inbox_push_as_on_and_needs_no_start_up_flag()
    {
        Assert.Null(Identity.Peek(home.Config));

        var (code, output) = Install(home.Config);

        Assert.Equal(0, code);
        var saved = Identity.Peek(home.Config)!;
        Assert.True(saved.Push);
        Assert.Equal(PushTransports.Inbox, saved.PushTransport);
        Assert.Contains("Push is ON through each session's inbox", output, StringComparison.Ordinal);
        Assert.DoesNotContain("--dangerously-load-development-channels", output, StringComparison.Ordinal);
        Assert.True(SavedSettings.Apply(home.Config).InboxPushEnabled);
        Assert.False(SavedSettings.Apply(home.Config).ChannelPushEnabled);
    }

    [Fact]
    public void The_channel_can_be_chosen_at_the_first_install_and_is_kept()
    {
        Install(home.Config, "--push", "channel");
        Install(home.Config);

        var saved = Identity.Peek(home.Config)!;
        Assert.True(saved.Push);
        Assert.Equal(PushTransports.Channel, saved.PushTransport);
        Assert.True(SavedSettings.Apply(home.Config).ChannelPushEnabled);
    }

    [Fact]
    public void An_install_after_a_login_still_counts_as_the_first_one()
    {
        Identity.Load(home.Config).Save(home.Config); // connecting creates the file with no push choice in it

        Install(home.Config);

        Assert.True(Identity.Peek(home.Config)!.Push);
    }

    [Fact]
    public void A_push_off_chosen_at_install_stays_off_through_later_installs()
    {
        Install(home.Config, "--push", "off");
        Assert.False(Identity.Peek(home.Config)!.Push);

        var (_, output) = Install(home.Config);

        Assert.False(Identity.Peek(home.Config)!.Push);
        Assert.False(SavedSettings.Apply(home.Config).PushEnabled);
        Assert.DoesNotContain("Push is ON", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_push_turned_off_by_config_stays_off_through_a_reinstall()
    {
        Install(home.Config);
        SavedSettings.SavePush(home.Config, on: false);

        Install(home.Config);

        Assert.False(Identity.Peek(home.Config)!.Push);
    }

    [Fact]
    public void An_environment_override_wins_and_the_first_install_saves_nothing()
    {
        var config = AgentConfig.FromEnvironment(k => k switch { "AP_AGENT_HOME" => home.Dir, "AP_PUSH" => "off", _ => null });

        Install(config);

        Assert.Null(Identity.Peek(config)?.Push);
        Assert.False(SavedSettings.Apply(config).PushEnabled);
    }
}
