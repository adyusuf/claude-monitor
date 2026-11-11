using Podium.Api.Remote;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>The pure rules of the exec-level contract (ADR-0021): what counts as a raise, the bytes that are signed, and what the API is willing to store.</summary>
public sealed class ExecLevelContractTests
{
    [Theory]
    [InlineData(ExecLevels.Off, ExecLevels.Argv, true)]
    [InlineData(ExecLevels.Off, ExecLevels.Shell, true)]
    [InlineData(ExecLevels.Argv, ExecLevels.Shell, true)]
    [InlineData(ExecLevels.Argv, ExecLevels.Off, false)]
    [InlineData(ExecLevels.Shell, ExecLevels.Argv, false)]
    [InlineData(ExecLevels.Shell, ExecLevels.Off, false)]
    [InlineData(ExecLevels.Off, ExecLevels.Off, false)]
    [InlineData(ExecLevels.Argv, ExecLevels.Argv, false)]
    [InlineData(ExecLevels.Shell, ExecLevels.Shell, false)]
    [InlineData(null, ExecLevels.Argv, true)]
    [InlineData(null, ExecLevels.Off, false)]
    [InlineData("root", ExecLevels.Argv, true)]
    [InlineData(ExecLevels.Shell, "root", false)]
    public void Only_a_change_to_a_higher_level_is_a_raise_and_an_unknown_current_level_counts_as_off(string? from, string to, bool expected) =>
        Assert.Equal(expected, ExecLevelSigning.Raises(from, to));

    [Fact]
    public void The_signed_bytes_are_the_tag_the_machine_the_level_the_sequence_and_the_deadline_in_unix_seconds_one_per_line()
    {
        var agent = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var notAfter = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

        var payload = System.Text.Encoding.UTF8.GetString(ExecLevelSigning.Payload(agent, ExecLevels.Argv, 7, notAfter));

        // the web test of the same name pins the same text, so the browser and the API sign and check the same bytes
        Assert.Equal("ap-exec-level-v1\n11111111-2222-3333-4444-555555555555\nargv\n7\n1794398400", payload);
    }

    [Fact]
    public void A_deadline_differing_only_below_a_second_signs_the_same_bytes_as_the_web_signs_whole_seconds()
    {
        var agent = Guid.NewGuid();
        var whole = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(ExecLevelSigning.Payload(agent, ExecLevels.Argv, 1, whole), ExecLevelSigning.Payload(agent, ExecLevels.Argv, 1, whole.AddMilliseconds(900)));
        Assert.NotEqual(ExecLevelSigning.Payload(agent, ExecLevels.Argv, 1, whole), ExecLevelSigning.Payload(agent, ExecLevels.Argv, 1, whole.AddSeconds(1)));
    }

    [Fact]
    public void A_signed_change_lives_ten_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), ExecLevelSigning.MaxLifetime);
    }

    [Fact]
    public void The_report_to_store_keeps_a_known_state_with_its_sequence_and_a_negative_sequence_becomes_zero()
    {
        var kept = ExecLevelChanges.Read<ExecLevelReport>(ExecLevelChanges.Store(new ExecLevelReport(4, ExecLevelStates.Expired)));
        var clamped = ExecLevelChanges.Read<ExecLevelReport>(ExecLevelChanges.Store(new ExecLevelReport(-9, ExecLevelStates.Stale)));

        Assert.Equal(new ExecLevelReport(4, ExecLevelStates.Expired), kept);
        Assert.Equal(new ExecLevelReport(0, ExecLevelStates.Stale), clamped);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData("APPLIED")]
    public void The_report_to_store_is_nothing_for_a_state_outside_the_closed_set(string state) =>
        Assert.Null(ExecLevelChanges.Store(new ExecLevelReport(1, state)));

    [Fact]
    public void No_report_is_nothing_to_store()
    {
        Assert.Null(ExecLevelChanges.Store(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    public void A_stored_document_the_api_cannot_read_is_treated_as_none(string? json)
    {
        Assert.Null(ExecLevelChanges.Read<ExecLevelRequest>(json));
    }
}
