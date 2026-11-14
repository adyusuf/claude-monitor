using System.Net;
using System.Security.Cryptography;
using Podium.Api.Remote;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// How step-up attempts are booked against the account lock (F-154): an attempt is booked before its code is looked at, so codes sent at
/// once never get more than the limit checked.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class StepUpCountTests(ApiFactory api)
{
    private const int Limit = 5;

    private static string Wrong(int i) => "00000" + (i % 10);

    private static string NewSpki()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    }

    private static Task<HttpResponseMessage> RegisterKeyAsync(TestUser user, string spki, string? code) =>
        user.PostAsync("/api/me/approval-keys", new { name = "laptop", publicKey = spki, code });

    private async Task<int> WrongCheckedAsync(TestUser user)
    {
        await using var db = api.Db();
        return await db.AuditEvents.CountAsync(a => a.ActorUserId == user.Id && a.Action == AuditActions.SignInFailed);
    }

    [Fact]
    public async Task In_a_burst_of_wrong_codes_only_the_limit_is_ever_checked_and_a_right_code_after_it_is_refused()
    {
        var team = await RemoteKit.TeamAsync(api);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        api.Clock.Advance(TimeSpan.FromMinutes(11));
        var answers = await Task.WhenAll(Enumerable.Range(0, 3 * Limit).Select(i => RegisterKeyAsync(team.Owner, NewSpki(), Wrong(i))));

        // Every attempt past the limit is refused by the lock before its code is looked at.
        Assert.InRange(answers.Count(a => a.StatusCode != HttpStatusCode.TooManyRequests), 1, Limit - 1);
        Assert.Equal(Limit, await WrongCheckedAsync(team.Owner));
        await RemoteKit.AssertProblemAsync(await RegisterKeyAsync(team.Owner, NewSpki(), RemoteKit.TotpNow(api, secret)), HttpStatusCode.TooManyRequests, "account_locked");
    }

    [Fact]
    public async Task In_a_burst_with_one_right_code_the_right_one_is_refused_once_the_limit_came_first()
    {
        var team = await RemoteKit.TeamAsync(api);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        api.Clock.Advance(TimeSpan.FromMinutes(11));
        var sends = Enumerable.Range(0, 3 * Limit).Select(i => RegisterKeyAsync(team.Owner, NewSpki(), Wrong(i)))
            .Append(RegisterKeyAsync(team.Owner, NewSpki(), RemoteKit.TotpNow(api, secret)));
        var answers = await Task.WhenAll(sends);

        var checkedWrong = await WrongCheckedAsync(team.Owner);
        if (answers[^1].StatusCode == HttpStatusCode.TooManyRequests)
        {
            // The limit was booked before the right code: it was refused unchecked and no more than the limit was ever checked.
            Assert.InRange(answers.Count(a => a.StatusCode != HttpStatusCode.TooManyRequests), 1, Limit - 1);
            Assert.Equal(Limit, checkedWrong);
        }
        else
        {
            // The right code was booked within the limit (at most limit - 1 wrong ones before it) and started the count again (at most the
            // limit after it).
            Assert.Equal(HttpStatusCode.OK, answers[^1].StatusCode);
            Assert.InRange(checkedWrong, 0, 2 * Limit - 1);
        }
    }

    [Fact]
    public async Task A_right_code_starts_the_count_again_even_when_the_request_is_then_refused_for_another_reason()
    {
        var team = await RemoteKit.TeamAsync(api);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        api.Clock.Advance(TimeSpan.FromMinutes(11));
        var spki = NewSpki();
        Assert.Equal(HttpStatusCode.OK, (await RegisterKeyAsync(team.Owner, spki, RemoteKit.TotpNow(api, secret))).StatusCode);
        api.Clock.Advance(TimeSpan.FromSeconds(30));
        for (var i = 0; i < Limit - 1; i++)
        {
            await RemoteKit.AssertProblemAsync(await RegisterKeyAsync(team.Owner, NewSpki(), Wrong(i)), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        }

        // The same key again: the code is accepted, then the request is refused and saves nothing; the count must still start again.
        await RemoteKit.AssertProblemAsync(await RegisterKeyAsync(team.Owner, spki, RemoteKit.TotpNow(api, secret)), HttpStatusCode.Conflict, "approval_key_exists");
        api.Clock.Advance(TimeSpan.FromSeconds(30));
        await RemoteKit.AssertProblemAsync(await RegisterKeyAsync(team.Owner, NewSpki(), Wrong(9)), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
    }

    // ---- the password that deleting an account asks for (F-027) ------------------------------------------------------

    private static Task<HttpResponseMessage> DeleteAsync(TestUser user, string password, string? code = null) =>
        user.PostAsync("/api/me/delete", new { password, confirm = "DELETE", code });

    [Fact]
    public async Task Wrong_passwords_on_account_deletion_lock_at_the_limit_and_the_lock_then_refuses_the_right_password()
    {
        var user = await api.NewClient().SignedUpAsync("su-del-pw");
        for (var i = 0; i < Limit - 1; i++)
        {
            await RemoteKit.AssertInvalidAsync(await DeleteAsync(user, "wrong password " + i), "password", "invalid_credentials");
        }

        await RemoteKit.AssertProblemAsync(await DeleteAsync(user, "wrong password 9"), HttpStatusCode.TooManyRequests, "account_locked");
        await RemoteKit.AssertProblemAsync(await DeleteAsync(user, TestUser.Password), HttpStatusCode.TooManyRequests, "account_locked");
        Assert.Equal(HttpStatusCode.OK, (await user.Http.GetAsync("/api/me")).StatusCode);
        Assert.Equal(Limit, await WrongCheckedAsync(user));
    }

    [Fact]
    public async Task A_locked_account_answers_the_lock_before_the_deletion_password_is_looked_at()
    {
        var user = await api.NewClient().SignedUpAsync("su-del-locked");
        var secret = await RemoteKit.EnableTotpAsync(api, user);
        for (var i = 0; i < Limit; i++) await user.PostAsync("/api/me/mfa/disable", new { code = Wrong(i) });

        await RemoteKit.AssertProblemAsync(await DeleteAsync(user, "wrong password"), HttpStatusCode.TooManyRequests, "account_locked");
        await RemoteKit.AssertProblemAsync(await DeleteAsync(user, TestUser.Password, RemoteKit.TotpNow(api, secret)), HttpStatusCode.TooManyRequests, "account_locked");
    }

    [Fact]
    public async Task A_right_deletion_password_does_not_undo_the_wrong_codes()
    {
        var user = await api.NewClient().SignedUpAsync("su-del-mix");
        await RemoteKit.EnableTotpAsync(api, user);
        for (var i = 0; i < Limit - 1; i++)
        {
            await RemoteKit.AssertInvalidAsync(await DeleteAsync(user, TestUser.Password, Wrong(i)), "code", "invalid_code");
        }

        await RemoteKit.AssertProblemAsync(await DeleteAsync(user, TestUser.Password, Wrong(9)), HttpStatusCode.TooManyRequests, "account_locked");
    }

    // ---- the strict approval gate --------------------------------------------------------------------------------------

    [Fact]
    public async Task Approving_a_grant_on_a_strict_target_locks_at_the_limit_and_then_refuses_a_right_code()
    {
        var team = await ScriptKit.TeamAsync(api, "production", "application", ExecLevels.Shell);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        var grant = await RemoteKit.RequestGrantAsync(team.Requester, RemoteKit.GrantAsk(team.TargetId));
        var approve = $"/api/grants/{grant.Id}/approve";

        // A fresh sign-in does not count on a strict target, so every one of these is a code the gate checks.
        for (var i = 0; i < Limit - 1; i++)
        {
            await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync(approve, new { code = Wrong(i) }), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        }

        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync(approve, new { code = Wrong(9) }), HttpStatusCode.TooManyRequests, "account_locked");
        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync(approve, new { code = RemoteKit.TotpNow(api, secret) }), HttpStatusCode.TooManyRequests,
            "account_locked");
    }
}
