using System.Security.Cryptography;
using Podium.Agent.Approvals;
using Podium.Agent.Exec;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>
/// What the machine does with a remote-run level the owner asked for on the web (ADR-0021): it checks every raise itself against the keys pinned here,
/// whatever the API says, and reports one state per change (<c>applied</c>, <c>signature_refused</c>, <c>no_pinned_key</c>, <c>expired</c>, <c>invalid</c>).
/// </summary>
public sealed class ExecLevelChangesAgentTests : ExecLevelAgentFixture
{
    private static string Flip(string signature)
    {
        var bytes = Convert.FromBase64String(signature);
        bytes[10] ^= 1;
        return Convert.ToBase64String(bytes);
    }

    // ---- nothing to do -------------------------------------------------------------------------------------------------------

    [Fact]
    public void No_request_changes_nothing_and_reports_nothing()
    {
        Assert.False(Handle(null));

        Assert.Null(ExecLevelChanges.Reported(Store));
        Assert.Equal(ExecLevels.Off, LevelOnDisk());
    }

    [Fact]
    public void A_machine_that_is_not_connected_handles_nothing()
    {
        File.Delete(Home.Config.IdentityPath);

        Assert.False(Handle(Bare(ExecLevels.Argv, 1)));

        Assert.Null(ExecLevelChanges.Reported(Store));
    }

    // ---- a raise ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_raise_signed_by_a_pinned_owner_key_is_applied_and_reported_applied()
    {
        Pin();

        Assert.True(Handle(OwnerSigned(ExecLevels.Argv, 1)));

        Assert.Equal(ExecLevels.Argv, LevelOnDisk());
        Assert.Equal(new ExecLevelReport(1, ExecLevelStates.Applied), ExecLevelChanges.Reported(Store, Agent));
        Assert.Contains(ExecLevelStates.Applied, ExecLevelChanges.Describe(Store, Agent), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExecLevels.Off, ExecLevels.Shell)]
    [InlineData(ExecLevels.Argv, ExecLevels.Shell)]
    public void A_signed_raise_to_a_higher_level_is_applied_from_any_lower_one(string from, string to)
    {
        Pin();
        SaveIdentity(Agent, from);

        Handle(OwnerSigned(to, 1));

        Assert.Equal(to, LevelOnDisk());
    }

    [Fact]
    public void A_raise_with_no_key_pinned_on_the_machine_is_reported_no_pinned_key_and_nothing_changes_even_when_signed()
    {
        Assert.True(Handle(OwnerSigned(ExecLevels.Argv, 1)));

        Assert.Equal(ExecLevels.Off, LevelOnDisk());
        Assert.Equal(new ExecLevelReport(1, ExecLevelStates.NoPinnedKey), ExecLevelChanges.Reported(Store, Agent));
    }

    [Fact]
    public void A_raise_that_carries_no_signature_is_refused_even_though_a_key_is_pinned()
    {
        Pin();

        Handle(Bare(ExecLevels.Argv, 1));

        Assert.Equal(ExecLevels.Off, LevelOnDisk());
        Assert.Equal(new ExecLevelReport(1, ExecLevelStates.SignatureRefused), ExecLevelChanges.Reported(Store, Agent));
    }

    [Fact]
    public void A_raise_signed_by_a_key_that_is_not_pinned_is_refused()
    {
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ApproversStore.Add(Home.Config, Convert.ToBase64String(stranger.ExportSubjectPublicKeyInfo()), "other");

        Handle(OwnerSigned(ExecLevels.Argv, 1));

        Assert.Equal(ExecLevels.Off, LevelOnDisk());
        Assert.Equal(ExecLevelStates.SignatureRefused, ExecLevelChanges.Reported(Store, Agent)!.State);
    }

    [Theory]
    [InlineData("for another machine")]
    [InlineData("for another level")]
    [InlineData("flipped signature")]
    [InlineData("not base64")]
    public void A_raise_whose_signature_does_not_cover_exactly_this_change_is_refused(string flaw)
    {
        Pin();
        var good = OwnerSigned(ExecLevels.Argv, 1);
        var request = flaw switch
        {
            "for another machine" => OwnerSigned(ExecLevels.Argv, 1, forAgent: Guid.NewGuid()),
            "for another level" => OwnerSigned(ExecLevels.Argv, 1, signedLevel: ExecLevels.Shell),
            "flipped signature" => good with { Signature = Flip(good.Signature!) },
            "not base64" => good with { Signature = "%%%" },
            _ => throw new ArgumentOutOfRangeException(nameof(flaw), flaw, null),
        };

        Handle(request);

        Assert.Equal(ExecLevels.Off, LevelOnDisk());
        Assert.Equal(ExecLevelStates.SignatureRefused, ExecLevelChanges.Reported(Store, Agent)!.State);
    }

    [Fact]
    public void A_signed_change_replayed_for_a_new_login_of_the_machine_is_refused_because_the_signature_names_the_old_agent()
    {
        Pin();
        var request = OwnerSigned(ExecLevels.Argv, 1);
        SaveIdentity(NewLogin, ExecLevels.Off);

        Handle(request);

        Assert.Equal(ExecLevels.Off, LevelOnDisk());
        Assert.Equal(ExecLevelStates.SignatureRefused, ExecLevelChanges.Reported(Store)!.State);
    }

    // ---- a lowering -------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ExecLevels.Shell, ExecLevels.Argv)]
    [InlineData(ExecLevels.Shell, ExecLevels.Off)]
    [InlineData(ExecLevels.Argv, ExecLevels.Off)]
    public void A_lowering_is_applied_without_any_signature_or_pinned_key(string from, string to)
    {
        SaveIdentity(Agent, from);

        Handle(Bare(to, 1));

        Assert.Equal(to, LevelOnDisk());
        Assert.Equal(new ExecLevelReport(1, ExecLevelStates.Applied), ExecLevelChanges.Reported(Store, Agent));
    }

    // ---- the deadline and the level ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_change_that_arrives_after_its_deadline_is_reported_expired_and_not_applied()
    {
        Pin();
        var request = OwnerSigned(ExecLevels.Argv, 1, TimeSpan.FromMinutes(5));
        Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        Handle(request);

        Assert.Equal(ExecLevels.Off, LevelOnDisk());
        Assert.Equal(new ExecLevelReport(1, ExecLevelStates.Expired), ExecLevelChanges.Reported(Store, Agent));
    }

    [Fact]
    public void A_change_at_exactly_its_deadline_is_still_taken()
    {
        SaveIdentity(Agent, ExecLevels.Argv);
        var request = Bare(ExecLevels.Off, 1, TimeSpan.FromMinutes(5));
        Clock.Advance(TimeSpan.FromMinutes(5));

        Handle(request);

        Assert.Equal(ExecLevelStates.Applied, ExecLevelChanges.Reported(Store, Agent)!.State);
    }

    [Fact]
    public void A_deadline_further_ahead_than_the_lifetime_plus_the_clock_skew_is_expired()
    {
        SaveIdentity(Agent, ExecLevels.Argv);
        var tooFar = ExecLevelSigning.MaxLifetime + ExecLevelChanges.ClockSkew + TimeSpan.FromSeconds(1);

        Handle(Bare(ExecLevels.Off, 1, tooFar));

        Assert.Equal(ExecLevels.Argv, LevelOnDisk());
        Assert.Equal(ExecLevelStates.Expired, ExecLevelChanges.Reported(Store, Agent)!.State);
    }

    [Fact]
    public void A_deadline_exactly_the_lifetime_plus_the_clock_skew_ahead_is_still_accepted()
    {
        SaveIdentity(Agent, ExecLevels.Argv);

        Handle(Bare(ExecLevels.Off, 1, ExecLevelSigning.MaxLifetime + ExecLevelChanges.ClockSkew));

        Assert.Equal(ExecLevelStates.Applied, ExecLevelChanges.Reported(Store, Agent)!.State);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("")]
    [InlineData("SHELL")]
    public void A_level_the_machine_does_not_know_is_reported_invalid_and_never_applied(string level)
    {
        SaveIdentity(Agent, ExecLevels.Argv);

        Handle(Bare(level, 1));

        Assert.Equal(ExecLevels.Argv, LevelOnDisk());
        Assert.Equal(ExecLevelStates.Invalid, ExecLevelChanges.Reported(Store, Agent)!.State);
    }
}
