using System.Net;
using System.Security.Cryptography;
using Podium.Api.Remote;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// Codes asked outside sign-in (step-up, turning two-step sign-in off, deleting the account) count towards the same lock as the second
/// step (F-154, F-027, F-071): the wrong code that reaches the limit locks, a right code is then refused until the lock ends, and a right
/// code before the limit starts the count again.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class StepUpLockTests(ApiFactory api)
{
    private const string AccountLocked = "account_locked";
    private const int Limit = 5;

    private static string Wrong(int i) => "00000" + (i % 10);

    private static string NewSpki()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    }

    private static Task<HttpResponseMessage> RegisterKeyAsync(TestUser user, string spki, string? code) =>
        user.PostAsync("/api/me/approval-keys", new { name = "laptop", publicKey = spki, code });

    [Fact]
    public async Task Approving_a_run_locks_at_the_limit_and_then_refuses_a_right_code_and_asking_without_a_code_does_not_count()
    {
        var team = await RemoteKit.TeamAsync(api, ExecLevels.Shell);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        var req = RemoteKit.Shell(team.TargetId, RemoteKit.NewKey());
        var id = (await RemoteKit.CreateAsync(team.Requester, req)).Id;

        for (var i = 0; i < Limit; i++)
        {
            await RemoteKit.AssertProblemAsync(await RemoteKit.ApproveAsync(team.Owner, id, req), HttpStatusCode.Forbidden, RemoteErrors.MfaRequired);
        }

        for (var i = 0; i < Limit - 1; i++)
        {
            await RemoteKit.AssertProblemAsync(await RemoteKit.ApproveAsync(team.Owner, id, req, Wrong(i)), HttpStatusCode.Forbidden, RemoteErrors.MfaRequired);
        }

        await RemoteKit.AssertProblemAsync(await RemoteKit.ApproveAsync(team.Owner, id, req, Wrong(9)), HttpStatusCode.TooManyRequests, AccountLocked);
        await RemoteKit.AssertProblemAsync(await RemoteKit.ApproveAsync(team.Owner, id, req, RemoteKit.TotpNow(api, secret)), HttpStatusCode.TooManyRequests,
            AccountLocked);
        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, id));

        await using var db = api.Db();
        var rows = await db.AuditEvents.AsNoTracking().Where(a => a.ActorUserId == team.Owner.Id).ToListAsync();
        Assert.Single(rows, a => a.Action == AuditActions.AccountLocked);
        Assert.Equal(Limit, rows.Count(a => a.Action == AuditActions.SignInFailed && a.Detail!.RootElement.GetProperty("step").GetString() == "step_up"));
    }

    [Fact]
    public async Task Registering_an_approval_key_counts_wrong_codes_a_right_code_starts_again_and_the_limit_locks()
    {
        var team = await RemoteKit.TeamAsync(api);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        api.Clock.Advance(TimeSpan.FromMinutes(11));

        for (var i = 0; i < Limit - 1; i++)
        {
            await RemoteKit.AssertProblemAsync(await RegisterKeyAsync(team.Owner, NewSpki(), Wrong(i)), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        }

        Assert.Equal(HttpStatusCode.OK, (await RegisterKeyAsync(team.Owner, NewSpki(), RemoteKit.TotpNow(api, secret))).StatusCode);
        api.Clock.Advance(TimeSpan.FromSeconds(30));
        for (var i = 0; i < Limit - 1; i++)
        {
            await RemoteKit.AssertProblemAsync(await RegisterKeyAsync(team.Owner, NewSpki(), Wrong(i)), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        }

        await RemoteKit.AssertProblemAsync(await RegisterKeyAsync(team.Owner, NewSpki(), Wrong(9)), HttpStatusCode.TooManyRequests, AccountLocked);
        api.Clock.Advance(TimeSpan.FromSeconds(30));
        await RemoteKit.AssertProblemAsync(await RegisterKeyAsync(team.Owner, NewSpki(), RemoteKit.TotpNow(api, secret)), HttpStatusCode.TooManyRequests, AccountLocked);
    }

    [Fact]
    public async Task Answering_with_always_locks_at_the_limit_and_then_refuses_a_right_code()
    {
        var w = await ApprovalRuleKit.WorldAsync(api, "su-always");
        var secret = await RemoteKit.EnableTotpAsync(api, w.Owner);
        api.Clock.Advance(TimeSpan.FromMinutes(5));
        var request = await ApprovalRuleKit.AskAsync(w.Agent, w.Session, "Bash", new { command = "make build" }, waitSeconds: 600);
        api.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        for (var i = 0; i < Limit - 1; i++)
        {
            await RemoteKit.AssertProblemAsync(await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, remember: ApprovalDurations.Always, code: Wrong(i)),
                HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        }

        await RemoteKit.AssertProblemAsync(await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, remember: ApprovalDurations.Always, code: Wrong(9)),
            HttpStatusCode.TooManyRequests, AccountLocked);
        await RemoteKit.AssertProblemAsync(await ApprovalRuleKit.AnswerAsync(w.Owner, request.Id, remember: ApprovalDurations.Always,
            code: RemoteKit.TotpNow(api, secret)), HttpStatusCode.TooManyRequests, AccountLocked);
        Assert.Empty(await ApprovalRuleKit.RulesAsync(api, w.WorkspaceId));
    }

    [Fact]
    public async Task Turning_two_step_sign_in_off_locks_at_the_limit_until_the_lock_ends()
    {
        var user = await api.NewClient().SignedUpAsync("su-disable");
        var secret = await RemoteKit.EnableTotpAsync(api, user);
        for (var i = 0; i < Limit - 1; i++)
        {
            await RemoteKit.AssertInvalidAsync(await user.PostAsync("/api/me/mfa/disable", new { code = Wrong(i) }), "code", "invalid_code");
        }

        await RemoteKit.AssertProblemAsync(await user.PostAsync("/api/me/mfa/disable", new { code = Wrong(9) }), HttpStatusCode.TooManyRequests, AccountLocked);
        await RemoteKit.AssertProblemAsync(await user.PostAsync("/api/me/mfa/disable", new { code = RemoteKit.TotpNow(api, secret) }), HttpStatusCode.TooManyRequests,
            AccountLocked);
        Assert.True((await user.GetJsonAsync("/api/me")).GetProperty("mfaEnabled").GetBoolean());

        api.Clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync("/api/me/mfa/disable", new { code = RemoteKit.TotpNow(api, secret) })).StatusCode);
    }

    [Fact]
    public async Task Wrong_codes_sent_at_once_all_count()
    {
        var user = await api.NewClient().SignedUpAsync("su-burst");
        var secret = await RemoteKit.EnableTotpAsync(api, user);
        await Task.WhenAll(Enumerable.Range(0, 2 * Limit).Select(i => user.PostAsync("/api/me/mfa/disable", new { code = Wrong(i) })));
        await RemoteKit.AssertProblemAsync(await user.PostAsync("/api/me/mfa/disable", new { code = RemoteKit.TotpNow(api, secret) }), HttpStatusCode.TooManyRequests,
            AccountLocked);
    }

    [Fact]
    public async Task Deleting_the_account_locks_at_the_limit_and_then_refuses_a_right_code()
    {
        var user = await api.NewClient().SignedUpAsync("su-delete");
        var secret = await RemoteKit.EnableTotpAsync(api, user);
        for (var i = 0; i < Limit - 1; i++)
        {
            await RemoteKit.AssertInvalidAsync(await user.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE", code = Wrong(i) }),
                "code", "invalid_code");
        }

        await RemoteKit.AssertProblemAsync(await user.PostAsync("/api/me/delete", new { password = TestUser.Password, confirm = "DELETE", code = Wrong(9) }),
            HttpStatusCode.TooManyRequests, AccountLocked);
        await RemoteKit.AssertProblemAsync(await user.PostAsync("/api/me/delete",
            new { password = TestUser.Password, confirm = "DELETE", code = RemoteKit.TotpNow(api, secret) }), HttpStatusCode.TooManyRequests, AccountLocked);
        Assert.Equal(HttpStatusCode.OK, (await user.Http.GetAsync("/api/me")).StatusCode);
    }
}
