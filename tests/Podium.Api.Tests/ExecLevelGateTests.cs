using System.Net;
using System.Text.Json;
using Podium.Api.Remote;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// The re-authentication every exec-level change goes through (like a grant approval, ADR-0021), the audit row, who reads the pending request, and the
/// machine's report of what it did with the latest change.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class ExecLevelGateTests(ApiFactory api)
{
    private static async Task AssertNothingStoredAsync(RemoteTeam team)
    {
        var view = await ExecLevelKit.ViewAsync(team.Owner, team.TargetId);
        Assert.Equal(0, view.GetProperty("sequence").GetInt64());
        Assert.Null(await ExecLevelKit.DeliveredAsync(team.Target));
    }

    // ---- a strict machine (production or database) -------------------------------------------------------------------------

    [Fact]
    public async Task A_signed_raise_on_a_production_machine_needs_two_step_sign_in_and_without_it_is_refused_as_totp_required()
    {
        var team = await ScriptKit.TeamAsync(api, "production", "application", ExecLevels.Off);
        using var key = await ExecLevelKit.RegisterKeyAsync(team.Owner);

        var response = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Argv, 1, ExecLevelKit.Deadline(api), key));

        await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Forbidden, RemoteErrors.TotpRequired);
        await AssertNothingStoredAsync(team);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("000000")]
    public async Task A_signed_raise_on_a_production_machine_is_refused_without_a_fresh_valid_code_even_right_after_signing_in(string? code)
    {
        var team = await ScriptKit.TeamAsync(api, "production", "application", ExecLevels.Off);
        await RemoteKit.EnableTotpAsync(api, team.Owner);
        using var key = await ExecLevelKit.RegisterKeyAsync(team.Owner);

        var response = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Argv, 1, ExecLevelKit.Deadline(api), key, code));

        await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        await AssertNothingStoredAsync(team);
    }

    [Fact]
    public async Task A_signed_raise_on_a_database_machine_is_accepted_with_a_fresh_valid_code()
    {
        var team = await ScriptKit.TeamAsync(api, "test", "database", ExecLevels.Off);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        using var key = await ExecLevelKit.RegisterKeyAsync(team.Owner);
        var change = ExecLevelKit.Change(team.TargetId, ExecLevels.Argv, 1, ExecLevelKit.Deadline(api), key, RemoteKit.TotpNow(api, secret));

        var response = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, change);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1L, (await ExecLevelKit.DeliveredAsync(team.Target))!.Sequence);
    }

    // F-139: lowering a strict machine's level is the safe direction and needs only a recent sign-in, not the two-step step-up code.
    [Theory]
    [InlineData("production", "application")]
    [InlineData("test", "database")]
    public async Task A_lowering_on_a_strict_machine_needs_only_a_recent_sign_in_even_without_two_step_sign_in(string environment, string function)
    {
        var team = await ScriptKit.TeamAsync(api, environment, function, ExecLevels.Argv);

        var response = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Off, 1, ExecLevelKit.Deadline(api)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var delivered = (await ExecLevelKit.DeliveredAsync(team.Target))!;
        Assert.Equal((ExecLevels.Off, 1L), (delivered.Level, delivered.Sequence));
    }

    [Fact]
    public async Task A_lowering_on_a_strict_machine_with_two_step_sign_in_on_does_not_ask_for_a_code_after_a_recent_sign_in()
    {
        var team = await ScriptKit.TeamAsync(api, "production", "application", ExecLevels.Argv);
        await RemoteKit.EnableTotpAsync(api, team.Owner);

        var response = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Off, 1, ExecLevelKit.Deadline(api)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_lowering_on_a_strict_machine_after_an_old_sign_in_needs_a_valid_code_and_is_otherwise_refused()
    {
        var team = await ScriptKit.TeamAsync(api, "production", "application", ExecLevels.Argv);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        api.Clock.Advance(TimeSpan.FromMinutes(11));

        var without = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Off, 1, ExecLevelKit.Deadline(api)));
        var wrong = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Off, 1, ExecLevelKit.Deadline(api), code: "000000"));
        await RemoteKit.AssertProblemAsync(without, HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        await RemoteKit.AssertProblemAsync(wrong, HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        Assert.Null(await ExecLevelKit.DeliveredAsync(team.Target));

        var with = await ExecLevelKit.PutAsync(team.Owner, team.TargetId,
            ExecLevelKit.Change(team.TargetId, ExecLevels.Off, 1, ExecLevelKit.Deadline(api), code: RemoteKit.TotpNow(api, secret)));
        Assert.Equal(HttpStatusCode.OK, with.StatusCode);
    }

    // ---- an ordinary machine ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task On_an_ordinary_machine_a_recent_sign_in_is_enough_and_an_old_one_needs_a_code()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Shell);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        Assert.Equal(HttpStatusCode.OK, (await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Argv, 1, ExecLevelKit.Deadline(api)))).StatusCode);

        api.Clock.Advance(TimeSpan.FromMinutes(11));
        var without = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Off, 2, ExecLevelKit.Deadline(api)));
        var with = await ExecLevelKit.PutAsync(team.Owner, team.TargetId,
            ExecLevelKit.Change(team.TargetId, ExecLevels.Off, 2, ExecLevelKit.Deadline(api), code: RemoteKit.TotpNow(api, secret)));

        await RemoteKit.AssertProblemAsync(without, HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        Assert.Equal(HttpStatusCode.OK, with.StatusCode);
    }

    // ---- the audit row -----------------------------------------------------------------------------------------------------------

    private async Task<List<Data.AuditEvent>> AuditOfAsync(Guid agentId)
    {
        await using var db = api.Db();
        return await db.AuditEvents.AsNoTracking().Where(a => a.Action == "agent.exec_level_requested" && a.TargetId == agentId.ToString()).OrderBy(a => a.Id).ToListAsync();
    }

    [Fact]
    public async Task A_signed_raise_is_audited_with_the_level_the_sequence_and_that_it_was_signed_but_never_the_signature()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Off);
        using var key = await ExecLevelKit.RegisterKeyAsync(team.Owner);
        var change = ExecLevelKit.Change(team.TargetId, ExecLevels.Argv, 1, ExecLevelKit.Deadline(api), key);
        Assert.Equal(HttpStatusCode.OK, (await ExecLevelKit.PutAsync(team.Owner, team.TargetId, change)).StatusCode);

        var row = Assert.Single(await AuditOfAsync(team.TargetId));

        var detail = row.Detail!.RootElement;
        Assert.Equal((team.Owner.Id, team.WorkspaceId, "agent"), (row.ActorUserId, row.WorkspaceId, row.TargetType));
        Assert.Equal((ExecLevels.Argv, 1L, true), (detail.GetProperty("level").GetString(), detail.GetProperty("sequence").GetInt64(), detail.GetProperty("signed").GetBoolean()));
        Assert.DoesNotContain(change.Signature!, detail.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lowering_is_audited_as_not_signed_and_a_refused_change_leaves_no_row()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Argv);
        var refused = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Off, 5, ExecLevelKit.Deadline(api)));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Empty(await AuditOfAsync(team.TargetId));

        Assert.Equal(HttpStatusCode.OK, (await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Off, 1, ExecLevelKit.Deadline(api)))).StatusCode);

        var detail = Assert.Single(await AuditOfAsync(team.TargetId)).Detail!.RootElement;
        Assert.Equal((ExecLevels.Off, false), (detail.GetProperty("level").GetString(), detail.GetProperty("signed").GetBoolean()));
    }

    // ---- who reads the request ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_pending_request_is_shown_to_the_owner_and_to_admins_and_a_plain_member_sees_the_state_without_it()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Argv);
        Assert.Equal(HttpStatusCode.OK, (await ExecLevelKit.PutAsync(team.Owner, team.TargetId, ExecLevelKit.Change(team.TargetId, ExecLevels.Off, 1, ExecLevelKit.Deadline(api)))).StatusCode);

        var asOwner = await ExecLevelKit.ViewAsync(team.Owner, team.TargetId);
        var asAdmin = await ExecLevelKit.ViewAsync(team.Admin, team.TargetId);
        var asMember = await ExecLevelKit.ViewAsync(team.Other, team.TargetId);

        Assert.Equal(ExecLevels.Off, asOwner.GetProperty("pending").GetProperty("level").GetString());
        Assert.Equal(ExecLevels.Off, asAdmin.GetProperty("pending").GetProperty("level").GetString());
        Assert.Equal(JsonValueKind.Null, asMember.GetProperty("pending").ValueKind);
        Assert.Equal((ExecLevels.Argv, 1L), (asMember.GetProperty("execLevel").GetString(), asMember.GetProperty("sequence").GetInt64()));
    }

    [Fact]
    public async Task The_pending_view_never_carries_the_signature_bytes()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Off);
        using var key = await ExecLevelKit.RegisterKeyAsync(team.Owner);
        var change = ExecLevelKit.Change(team.TargetId, ExecLevels.Argv, 1, ExecLevelKit.Deadline(api), key);
        var put = await ExecLevelKit.PutAsync(team.Owner, team.TargetId, change);

        var shown = (await ExecLevelKit.GetAsync(team.Owner, team.TargetId)).Content.ReadAsStringAsync();

        Assert.DoesNotContain(change.Signature!, await put.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(change.Signature!, await shown, StringComparison.Ordinal);
    }

    // ---- what the machine reports ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task What_the_machine_reports_it_did_with_the_change_is_shown_with_the_time_and_an_unknown_state_is_ignored()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Off);
        await ExecLevelKit.ReportAsync(team.Target, ExecLevels.Off, new ExecLevelReport(1, ExecLevelStates.NoPinnedKey));
        var first = (await ExecLevelKit.ViewAsync(team.Owner, team.TargetId)).GetProperty("report");
        Assert.Equal((1L, ExecLevelStates.NoPinnedKey), (first.GetProperty("sequence").GetInt64(), first.GetProperty("state").GetString()));

        await ExecLevelKit.ReportAsync(team.Target, ExecLevels.Off, new ExecLevelReport(2, "something-new"));

        var view = await ExecLevelKit.ViewAsync(team.Other, team.TargetId);
        Assert.Equal(ExecLevelStates.NoPinnedKey, view.GetProperty("report").GetProperty("state").GetString());
        Assert.Equal(api.Clock.GetUtcNow(), view.GetProperty("reportAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData(ExecLevelStates.Applied)]
    [InlineData(ExecLevelStates.SignatureRefused)]
    [InlineData(ExecLevelStates.NoPinnedKey)]
    [InlineData(ExecLevelStates.Expired)]
    [InlineData(ExecLevelStates.NeedsAdmin)]
    [InlineData(ExecLevelStates.Invalid)]
    [InlineData(ExecLevelStates.Stale)]
    public async Task Every_state_of_the_closed_set_is_stored_as_the_machine_reported_it(string state)
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Off);

        await ExecLevelKit.ReportAsync(team.Target, ExecLevels.Off, new ExecLevelReport(3, state));

        var report = (await ExecLevelKit.ViewAsync(team.Owner, team.TargetId)).GetProperty("report");
        Assert.Equal((3L, state), (report.GetProperty("sequence").GetInt64(), report.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task A_profile_with_no_report_from_an_older_agent_keeps_the_report_already_stored()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Off);
        await ExecLevelKit.ReportAsync(team.Target, ExecLevels.Off, new ExecLevelReport(1, ExecLevelStates.Applied));

        await RemoteKit.ProfileAsync(team.Target, ExecLevels.Off);

        var report = (await ExecLevelKit.ViewAsync(team.Owner, team.TargetId)).GetProperty("report");
        Assert.Equal((1L, ExecLevelStates.Applied), (report.GetProperty("sequence").GetInt64(), report.GetProperty("state").GetString()));
    }
}
