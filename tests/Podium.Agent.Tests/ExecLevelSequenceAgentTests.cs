using Podium.Agent.Exec;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The sequence of an exec-level change on the machine (ADR-0021): each one is handled once, an older one is stale, and the count belongs to one agent id.</summary>
public sealed class ExecLevelSequenceAgentTests : ExecLevelAgentFixture
{
    [Fact]
    public void A_sequence_below_the_last_handled_one_is_reported_stale_once_and_is_not_applied()
    {
        SaveIdentity(Agent, ExecLevels.Shell);
        Handle(Bare(ExecLevels.Argv, 5));
        var old = Bare(ExecLevels.Off, 3);

        var first = Handle(old);
        var again = Handle(old);

        Assert.True(first);
        Assert.False(again); // the API keeps delivering it; it is reported once
        Assert.Equal(ExecLevels.Argv, LevelOnDisk());
        Assert.Equal(new ExecLevelReport(3, ExecLevelStates.Stale), ExecLevelChanges.Reported(Store, Agent));
    }

    [Fact]
    public void A_stale_change_does_not_move_the_handled_sequence_so_the_next_one_in_order_still_applies()
    {
        SaveIdentity(Agent, ExecLevels.Shell);
        Handle(Bare(ExecLevels.Argv, 5));
        Handle(Bare(ExecLevels.Off, 3));

        Handle(Bare(ExecLevels.Off, 6));

        Assert.Equal(ExecLevels.Off, LevelOnDisk());
        Assert.Equal(new ExecLevelReport(6, ExecLevelStates.Applied), ExecLevelChanges.Reported(Store, Agent));
    }

    [Fact]
    public void An_unsigned_change_that_jumps_more_than_a_thousand_ahead_is_reported_stale_and_not_applied()
    {
        SaveIdentity(Agent, ExecLevels.Shell);
        Handle(Bare(ExecLevels.Shell, 2));

        Handle(Bare(ExecLevels.Off, 2 + ExecLevelChanges.UnsignedJumpMax + 1));

        Assert.Equal(ExecLevels.Shell, LevelOnDisk());
        Assert.Equal(ExecLevelStates.Stale, ExecLevelChanges.Reported(Store, Agent)!.State);
    }

    [Fact]
    public void An_unsigned_change_exactly_a_thousand_ahead_is_still_within_what_an_offline_machine_missed()
    {
        SaveIdentity(Agent, ExecLevels.Shell);
        Handle(Bare(ExecLevels.Shell, 2));

        Handle(Bare(ExecLevels.Off, 2 + ExecLevelChanges.UnsignedJumpMax));

        Assert.Equal(ExecLevels.Off, LevelOnDisk());
        Assert.Equal(ExecLevelStates.Applied, ExecLevelChanges.Reported(Store, Agent)!.State);
    }

    [Fact]
    public void A_signed_change_may_jump_far_ahead_because_only_the_owner_could_have_made_it()
    {
        Pin();

        Handle(OwnerSigned(ExecLevels.Argv, 50_000));

        Assert.Equal(ExecLevels.Argv, LevelOnDisk());
        Assert.Equal(new ExecLevelReport(50_000, ExecLevelStates.Applied), ExecLevelChanges.Reported(Store, Agent));
    }

    [Fact]
    public void A_sequence_that_was_already_handled_is_silent_and_is_not_applied_a_second_time()
    {
        SaveIdentity(Agent, ExecLevels.Shell);
        var request = Bare(ExecLevels.Argv, 1);
        Assert.True(Handle(request));
        SaveIdentity(Agent, ExecLevels.Shell); // the level was set back on the machine by hand

        var again = Handle(request);

        Assert.False(again);
        Assert.Equal(ExecLevels.Shell, LevelOnDisk());
    }

    [Fact]
    public void A_refused_change_also_uses_up_its_sequence_so_it_is_not_tried_again()
    {
        Pin();
        var request = Bare(ExecLevels.Argv, 1);
        Assert.True(Handle(request));

        var again = Handle(request);

        Assert.False(again);
        Assert.Equal(ExecLevelStates.SignatureRefused, ExecLevelChanges.Reported(Store, Agent)!.State);
    }

    [Fact]
    public void The_sequence_is_kept_per_agent_so_a_new_login_starts_the_count_over_and_the_old_report_is_not_shown_for_it()
    {
        SaveIdentity(Agent, ExecLevels.Shell);
        Handle(Bare(ExecLevels.Argv, 9));
        SaveIdentity(NewLogin, ExecLevels.Shell);
        Assert.Null(ExecLevelChanges.Reported(Store, NewLogin));

        Handle(Bare(ExecLevels.Off, 1));

        Assert.Equal(ExecLevels.Off, LevelOnDisk());
        Assert.Equal(new ExecLevelReport(1, ExecLevelStates.Applied), ExecLevelChanges.Reported(Store, NewLogin));
        Assert.Null(ExecLevelChanges.Reported(Store, Agent));
    }

    [Fact]
    public void A_failure_saving_the_level_leaves_the_sequence_unrecorded_is_logged_and_the_next_pass_applies_it()
    {
        SaveIdentity(Agent, ExecLevels.Shell);
        var blocker = Home.Config.IdentityPath + ".tmp";
        Directory.CreateDirectory(blocker); // agent.json cannot be written: the file it is written through is a folder
        var change = Bare(ExecLevels.Off, 1);

        Assert.False(Handle(change));
        Assert.Equal(ExecLevels.Shell, LevelOnDisk());
        Assert.Null(ExecLevelChanges.Reported(Store, Agent));
        Assert.Contains("exec level change failed (", File.ReadAllText(Home.Config.LogPath), StringComparison.Ordinal);

        Directory.Delete(blocker);
        Assert.True(Handle(change));
        Assert.Equal(ExecLevels.Off, LevelOnDisk());
        Assert.Equal(new ExecLevelReport(1, ExecLevelStates.Applied), ExecLevelChanges.Reported(Store, Agent));
    }
}
