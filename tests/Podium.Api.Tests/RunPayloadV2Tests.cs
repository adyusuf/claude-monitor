using System.Net;
using System.Security.Cryptography;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// The repository is signed with the run (ADR-0020 §4 rule 6, payload v2): the API checks a signed approval over the run's repository as it stores
/// it, so a label changed after signing or a v1 signature for a labelled run is refused; and a run keeps a label only when its target reports that
/// repository, in the target's own spelling.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class RunPayloadV2Tests(ApiFactory api)
{
    private const string Reported = "git:git.example.invalid/Team/Shop";

    private sealed class Key : IDisposable
    {
        private readonly ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string Spki => Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        public string Id => ApprovalSigning.Fingerprint(ecdsa.ExportSubjectPublicKeyInfo());

        public string Sign(byte[] payload) =>
            Convert.ToBase64String(ecdsa.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        public void Dispose() => ecdsa.Dispose();
    }

    /// <summary>A team whose target reports <see cref="Reported"/>, and whose owner registered the key.</summary>
    private async Task<RemoteTeam> TeamAsync(Key key)
    {
        var team = await RemoteKit.TeamAsync(api);
        (await team.Owner.PostAsync("/api/me/approval-keys", new { name = "laptop", publicKey = key.Spki })).EnsureSuccessStatusCode();
        await using var db = api.Db();
        db.MachineProjects.Add(new MachineProject { AgentId = team.TargetId, ProjectKey = Reported, WorkspaceId = team.WorkspaceId, Name = "shop", ClaudeTrusted = false });
        await db.SaveChangesAsync();
        return team;
    }

    private static async Task<(Guid Id, RunCreate Req)> RunAsync(RemoteTeam team, string? projectKey)
    {
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey()) with { ProjectKey = projectKey };
        return ((await RemoteKit.CreateAsync(team.Requester, req)).Id, req);
    }

    /// <summary>The owner approves with a signature over the payload made for the run as it is stored now, v2 over <paramref name="signedKey"/> or v1 when null.</summary>
    private async Task<HttpResponseMessage> ApproveSignedAsync(RemoteTeam team, Key key, Guid id, RunCreate req, string? signedKey)
    {
        var row = await RemoteKit.RunRowAsync(api, id);
        var hash = RemoteKit.HashOf(req);
        var payload = signedKey is null
            ? ApprovalSigning.Payload(ApprovalSigning.KindRun, id, team.TargetId, hash, row.ExpiresAt)
            : ApprovalSigning.PayloadV2(ApprovalSigning.KindRun, id, team.TargetId, hash, row.ExpiresAt, signedKey);
        return await team.Owner.PostAsync($"/api/runs/{id}/approve", new { hash, keyId = key.Id, signature = key.Sign(payload) });
    }

    private async Task RelabelAsync(Guid id, string? projectKey)
    {
        await using var db = api.Db();
        await db.RemoteRuns.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ProjectKey, projectKey));
    }

    [Fact]
    public async Task A_run_keeps_a_label_only_when_its_target_reports_that_repository_and_then_in_the_targets_spelling()
    {
        using var key = new Key();
        var team = await TeamAsync(key);

        var otherCase = await RunAsync(team, "git:git.example.invalid/team/shop");
        var unreported = await RunAsync(team, "git:git.example.invalid/team/other");
        var none = await RunAsync(team, null);

        Assert.Equal(Reported, (await RemoteKit.RunRowAsync(api, otherCase.Id)).ProjectKey);
        Assert.Null((await RemoteKit.RunRowAsync(api, unreported.Id)).ProjectKey);
        Assert.Null((await RemoteKit.RunRowAsync(api, none.Id)).ProjectKey);
    }

    [Fact]
    public async Task A_labelled_run_signed_with_its_repository_is_approved_and_keeps_the_signature()
    {
        using var key = new Key();
        var team = await TeamAsync(key);
        var (id, req) = await RunAsync(team, Reported);

        var response = await ApproveSignedAsync(team, key, id, req, Reported);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var row = await RemoteKit.RunRowAsync(api, id);
        Assert.Equal((RunStatuses.Approved, key.Id), (row.Status, row.ApprovalKeyId));
    }

    [Fact]
    public async Task A_run_relabelled_after_the_owner_signed_is_refused_and_stays_pending()
    {
        using var key = new Key();
        var team = await TeamAsync(key);
        var (id, req) = await RunAsync(team, Reported);
        await RelabelAsync(id, "git:git.example.invalid/team/other");

        var response = await ApproveSignedAsync(team, key, id, req, Reported);

        await RemoteKit.AssertProblemAsync(response, HttpStatusCode.Forbidden, ApprovalErrors.Invalid);
        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, id));
    }

    [Fact]
    public async Task A_v1_signature_never_covers_a_labelled_run_and_a_v2_one_never_covers_an_unlabelled_run()
    {
        using var key = new Key();
        var team = await TeamAsync(key);
        var labelled = await RunAsync(team, Reported);
        var plain = await RunAsync(team, null);

        var v1 = await ApproveSignedAsync(team, key, labelled.Id, labelled.Req, null);
        var v2 = await ApproveSignedAsync(team, key, plain.Id, plain.Req, Reported);
        var plainV1 = await ApproveSignedAsync(team, key, plain.Id, plain.Req, null);

        await RemoteKit.AssertProblemAsync(v1, HttpStatusCode.Forbidden, ApprovalErrors.Invalid);
        await RemoteKit.AssertProblemAsync(v2, HttpStatusCode.Forbidden, ApprovalErrors.Invalid);
        Assert.Equal(HttpStatusCode.NoContent, plainV1.StatusCode);
    }
}
