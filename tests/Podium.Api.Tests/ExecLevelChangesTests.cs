using System.Net;
using System.Text.Json;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// The owner's change of a machine's remote-run level from the web (ADR-0021), the API side: who may ask, which level and deadline are accepted, the
/// sequence compare-and-set, and the rule that a raise carries a signature of one of the owner's live keys while a lowering carries none.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class ExecLevelChangesTests(ApiFactory api)
{
    private static long SequenceOf(JsonElement view) => view.GetProperty("sequence").GetInt64();

    /// <summary>Nothing was stored: the sequence is still 0 and there is no pending request.</summary>
    private static async Task AssertUntouchedAsync(RemoteTeam team)
    {
        var view = await ExecLevelKit.ViewAsync(team.Owner, team.TargetId);
        Assert.Equal(0, SequenceOf(view));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("pending").ValueKind);
        Assert.Null(await ExecLevelKit.DeliveredAsync(team.Target));
    }

    // ---- who may ask -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_the_machines_owner_can_change_its_level_a_member_and_even_the_workspace_admin_are_told_it_is_not_found(bool admin)
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Argv);
        var caller = admin ? team.Admin : team.Other;

        var response = await ExecLevelKit.PutAsync(caller, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Off, 1, ExecLevelKit.Deadline(api)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertUntouchedAsync(team);
    }

    [Fact]
    public async Task Somebody_outside_the_workspace_can_neither_change_nor_read_the_level()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Argv);
        var stranger = await api.NewClient().SignedUpAsync("el-stranger");

        var put = await ExecLevelKit.PutAsync(stranger, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Off, 1, ExecLevelKit.Deadline(api)));
        var get = await ExecLevelKit.GetAsync(stranger, team.TargetId);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (put.StatusCode, get.StatusCode));
        await AssertUntouchedAsync(team);
    }

    [Fact]
    public async Task A_machine_that_does_not_exist_is_not_found()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Argv);

        var response = await ExecLevelKit.PutAsync(team.Owner, Guid.NewGuid(), ExecLevelKit.Change(team.TargetId, ExecLevels.Off, 1, ExecLevelKit.Deadline(api)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- the level and the deadline -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("root", 300)]
    [InlineData("", 300)]
    [InlineData("SHELL", 300)]
    [InlineData(ExecLevels.Off, 0)]
    [InlineData(ExecLevels.Off, -60)]
    [InlineData(ExecLevels.Off, 601)]
    public async Task An_unknown_level_a_deadline_that_is_not_ahead_and_a_deadline_beyond_ten_minutes_are_refused_as_invalid(string level, int secondsAhead)
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Argv);
        var notAfter = api.Clock.GetUtcNow().AddSeconds(secondsAhead);

        var response = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, level, 1, notAfter));

        await RemoteKit.AssertInvalidAsync(response, "level", ExecLevelErrors.Invalid);
        await AssertUntouchedAsync(team);
    }

    [Fact]
    public async Task A_deadline_of_exactly_ten_minutes_is_the_longest_accepted()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Argv);
        var notAfter = api.Clock.GetUtcNow() + ExecLevelSigning.MaxLifetime;

        var response = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Off, 1, notAfter));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pending = (await ExecLevelKit.ViewAsync(team.Owner, team.TargetId)).GetProperty("pending");
        Assert.Equal(notAfter, pending.GetProperty("notAfter").GetDateTimeOffset());
    }

    // ---- the sequence ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(1000)]
    public async Task A_sequence_that_is_not_the_current_one_plus_one_is_a_conflict_that_stores_nothing(long sequence)
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Argv);

        var response = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Off, sequence, ExecLevelKit.Deadline(api)));

        await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Conflict, ExecLevelErrors.Changed);
        await AssertUntouchedAsync(team);
    }

    [Fact]
    public async Task Each_accepted_change_moves_the_sequence_on_by_one_so_a_replay_of_the_same_one_is_a_conflict()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Shell);
        var first = ExecLevelKit.Change(team.TargetId, ExecLevels.Argv, 1, ExecLevelKit.Deadline(api));
        Assert.Equal(HttpStatusCode.OK, (await ExecLevelKit.PutAsync(team.Owner, team.TargetId, first)).StatusCode);

        var replay = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, first);
        var next = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Off, 2, ExecLevelKit.Deadline(api)));

        await RemoteKit.AssertProblemAsync(replay, HttpStatusCode.Conflict, ExecLevelErrors.Changed);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        var view = await ExecLevelKit.ViewAsync(team.Owner, team.TargetId);
        Assert.Equal((2L, ExecLevels.Off), (SequenceOf(view), view.GetProperty("pending").GetProperty("level").GetString()));
    }

    // ---- a raise needs a signature, a lowering does not --------------------------------------------------------------------

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, "c2ln")]
    [InlineData("fingerprint", null)]
    public async Task A_raise_without_both_a_key_id_and_a_signature_is_refused_as_missing_its_signature(string? keyId, string? signature)
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Off);
        var change = new ExecLevelChange(ExecLevels.Argv, 1, ExecLevelKit.Deadline(api), keyId, signature);

        var response = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, change);

        await RemoteKit.AssertInvalidAsync(response, "signature", ExecLevelErrors.SignatureRequired);
        await AssertUntouchedAsync(team);
    }

    [Theory]
    [InlineData(ExecLevels.Off, ExecLevels.Argv)]
    [InlineData(ExecLevels.Off, ExecLevels.Shell)]
    [InlineData(ExecLevels.Argv, ExecLevels.Shell)]
    public async Task Every_step_up_needs_a_signature_not_only_the_first(string from, string to)
    {
        var team = await RemoteKit.TeamAsync(api, from);

        var response = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, to, 1, ExecLevelKit.Deadline(api)));

        await RemoteKit.AssertInvalidAsync(response, "signature", ExecLevelErrors.SignatureRequired);
    }

    [Theory]
    [InlineData(ExecLevels.Shell, ExecLevels.Argv)]
    [InlineData(ExecLevels.Shell, ExecLevels.Off)]
    [InlineData(ExecLevels.Argv, ExecLevels.Off)]
    public async Task A_lowering_is_accepted_with_no_signature_and_is_stored_unsigned(string from, string to)
    {
        var team = await RemoteKit.TeamAsync(api, from);

        var response = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, to, 1, ExecLevelKit.Deadline(api)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var delivered = await ExecLevelKit.DeliveredAsync(team.Target);
        Assert.Equal((to, 1L), (delivered!.Level, delivered.Sequence));
        Assert.Null(delivered.KeyId);
        Assert.Null(delivered.Signature);
        Assert.False((await ExecLevelKit.ViewAsync(team.Owner, team.TargetId)).GetProperty("pending").GetProperty("isSigned").GetBoolean());
    }

    [Fact]
    public async Task A_signature_sent_with_a_lowering_is_not_kept_or_forwarded_to_the_machine()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Argv);
        var change = new ExecLevelChange(ExecLevels.Off, 1, ExecLevelKit.Deadline(api), "any-key", "any-signature");

        Assert.Equal(HttpStatusCode.OK, (await ExecLevelKit.PutAsync(team.Owner, team.TargetId, change)).StatusCode);

        var delivered = await ExecLevelKit.DeliveredAsync(team.Target);
        Assert.Null(delivered!.KeyId);
        Assert.Null(delivered.Signature);
    }

    [Fact]
    public async Task A_raise_signed_with_a_registered_key_is_accepted_stored_with_its_signature_and_forwarded_but_the_level_itself_does_not_change()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Off);
        using var key = await ExecLevelKit.RegisterKeyAsync(team.Owner);
        var change = ExecLevelKit.Change(team.TargetId, ExecLevels.Argv, 1, ExecLevelKit.Deadline(api), key);

        var response = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, change);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var delivered = await ExecLevelKit.DeliveredAsync(team.Target);
        Assert.Equal((ExecLevels.Argv, 1L, key.Id, change.Signature), (delivered!.Level, delivered.Sequence, delivered.KeyId, delivered.Signature));
        var view = await ExecLevelKit.ViewAsync(team.Owner, team.TargetId);
        Assert.Equal((ExecLevels.Off, 1L, true), (view.GetProperty("execLevel").GetString(), SequenceOf(view), view.GetProperty("pending").GetProperty("isSigned").GetBoolean()));
    }

    [Fact]
    public async Task The_raise_is_judged_against_the_level_the_machine_reports_not_the_one_last_asked_for()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Off);
        using var key = await ExecLevelKit.RegisterKeyAsync(team.Owner);
        Assert.Equal(HttpStatusCode.OK, (await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Argv, 1, ExecLevelKit.Deadline(api), key))).StatusCode);

        // The machine has not applied it (it still reports off), so going to shell is still a raise and still needs a signature.
        var unsigned = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Shell, 2, ExecLevelKit.Deadline(api)));

        await RemoteKit.AssertInvalidAsync(unsigned, "signature", ExecLevelErrors.SignatureRequired);
    }

    // ---- a signature the API refuses ---------------------------------------------------------------------------------------

    /// <summary>A change for a machine at off that is signed wrongly in the way the case names; nothing about it is valid except that one flaw.</summary>
    private async Task<(RemoteTeam Team, ExecLevelChange Change)> ForgedAsync(string flaw)
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Off);
        var mine = await ExecLevelKit.RegisterKeyAsync(team.Owner);
        var other = await ExecLevelKit.RegisterKeyAsync(team.Other);
        var unregistered = new ExecKey();
        var notAfter = ExecLevelKit.Deadline(api);
        var (id, level, sequence) = (team.TargetId, ExecLevels.Argv, 1L);
        var change = flaw switch
        {
            "unregistered key" => ExecLevelKit.Change(id, level, sequence, notAfter, unregistered),
            "another user's key" => ExecLevelKit.Change(id, level, sequence, notAfter, other),
            "signed for another level" => new ExecLevelChange(level, sequence, notAfter, mine.Id, mine.Sign(id, ExecLevels.Shell, sequence, notAfter)),
            "signed for another sequence" => new ExecLevelChange(level, sequence, notAfter, mine.Id, mine.Sign(id, level, 2, notAfter)),
            "signed for another deadline" => new ExecLevelChange(level, sequence, notAfter, mine.Id, mine.Sign(id, level, sequence, notAfter.AddSeconds(1))),
            "signed for another machine" => new ExecLevelChange(level, sequence, notAfter, mine.Id, mine.Sign(Guid.NewGuid(), level, sequence, notAfter)),
            "not base64" => new ExecLevelChange(level, sequence, notAfter, mine.Id, "%%%"),
            "oversized" => new ExecLevelChange(level, sequence, notAfter, mine.Id, new string('A', 10_000)),
            "oversized key id" => new ExecLevelChange(level, sequence, notAfter, new string('a', 10_000), mine.Sign(id, level, sequence, notAfter)),
            _ => throw new ArgumentOutOfRangeException(nameof(flaw), flaw, null),
        };
        return (team, change);
    }

    [Theory]
    [InlineData("unregistered key")]
    [InlineData("another user's key")]
    [InlineData("signed for another level")]
    [InlineData("signed for another sequence")]
    [InlineData("signed for another deadline")]
    [InlineData("signed for another machine")]
    [InlineData("not base64")]
    [InlineData("oversized")]
    [InlineData("oversized key id")]
    public async Task A_raise_whose_signature_does_not_verify_under_one_of_the_owners_keys_is_forbidden_and_stores_nothing(string flaw)
    {
        var (team, change) = await ForgedAsync(flaw);

        var response = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, change);

        await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Forbidden, ApprovalErrors.Invalid);
        await AssertUntouchedAsync(team);
    }

    [Fact]
    public async Task A_retired_key_no_longer_signs_a_raise()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Off);
        using var key = await ExecLevelKit.RegisterKeyAsync(team.Owner);
        var keyRow = (await team.Owner.GetJsonAsync("/api/me/approval-keys")).EnumerateArray().Single(k => k.GetProperty("fingerprint").GetString() == key.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/me/approval-keys/{keyRow.GetProperty("id").GetGuid()}/retire")).StatusCode);

        var response = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Argv, 1, ExecLevelKit.Deadline(api), key));

        await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Forbidden, ApprovalErrors.Invalid);
        await AssertUntouchedAsync(team);
    }
}
