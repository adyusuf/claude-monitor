using System.Net;
using System.Security.Cryptography;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Remote;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>Registering an approval key is safe against a double submit and parallel calls (F-195): one live key per fingerprint, at most the limit, never a 500.</summary>
[Collection(ApiGroup.Name)]
public sealed class ApprovalKeyRaceTests(ApiFactory api)
{
    private static byte[] NewSpki()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return ecdsa.ExportSubjectPublicKeyInfo();
    }

    private static Task<HttpResponseMessage> RegisterAsync(TestUser user, byte[] spki) =>
        user.PostAsync("/api/me/approval-keys", new { name = "laptop", publicKey = Convert.ToBase64String(spki) });

    private async Task<List<ApprovalKey>> LiveAsync(Guid userId)
    {
        await using var db = api.Db();
        return await db.ApprovalKeys.AsNoTracking().Where(k => k.UserId == userId && k.RetiredAt == null).ToListAsync();
    }

    [Fact]
    public async Task The_same_key_registered_a_moment_earlier_by_another_call_answers_409_and_leaves_one_live_key()
    {
        var team = await RemoteKit.TeamAsync(api);
        var spki = NewSpki();
        var fault = new CommandFault("INSERT INTO approval_keys");
        await using var host = HostKit.WithInterceptor(api, fault);
        var owner = await HostKit.SignInAsync(api, host, team.Owner);
        // The other call's insert lands between this call's check and its own insert.
        fault.Before = () =>
        {
            using var db = api.Db();
            db.ApprovalKeys.Add(new ApprovalKey { UserId = team.Owner.Id, Name = "first", PublicKey = Convert.ToBase64String(spki), Fingerprint = ApprovalSigning.Fingerprint(spki), CreatedAt = api.Clock.GetUtcNow() });
            db.SaveChanges();
        };

        var response = await RegisterAsync(owner, spki);

        await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Conflict, "approval_key_exists");
        Assert.Single(await LiveAsync(team.Owner.Id));
    }

    [Fact]
    public async Task Parallel_registrations_of_different_keys_never_pass_the_limit()
    {
        var team = await RemoteKit.TeamAsync(api);
        await using (var db = api.Db())
        {
            for (var i = 0; i < ApprovalKeyEndpoints.ActiveMax - 1; i++)
            {
                db.ApprovalKeys.Add(new ApprovalKey { UserId = team.Owner.Id, Name = "k" + i, PublicKey = "x", Fingerprint = Guid.NewGuid().ToString("N"), CreatedAt = api.Clock.GetUtcNow() });
            }

            await db.SaveChangesAsync();
        }

        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => RegisterAsync(team.Owner, NewSpki()))));

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        foreach (var refused in answers.Where(a => a.StatusCode != HttpStatusCode.OK))
        {
            await RemoteKit.AssertProblemAsync(refused, HttpStatusCode.Conflict, RemoteErrors.ApprovalKeyLimit);
        }

        Assert.Equal(ApprovalKeyEndpoints.ActiveMax, (await LiveAsync(team.Owner.Id)).Count);
    }
}
