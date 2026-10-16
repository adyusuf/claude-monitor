using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ClaudeMonitor.Api.Background;
using ClaudeMonitor.Api.Config;
using ClaudeMonitor.Api.Data;
using ClaudeMonitor.Api.Endpoints;
using ClaudeMonitor.Api.Remote;
using ClaudeMonitor.Api.Streaming;
using ClaudeMonitor.Api.Tests.Infrastructure;
using ClaudeMonitor.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeMonitor.Api.Tests;

/// <summary>Signed approvals (ADR-0010 §4): the keys people register, the signature the API checks and forwards, and what a target that requires them is never given.</summary>
[Collection(ApiGroup.Name)]
public sealed class ApprovalKeyTests(ApiFactory api)
{
    private sealed class Key : IDisposable
    {
        private readonly ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string Spki => Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        public string Id => ApprovalSigning.Fingerprint(ecdsa.ExportSubjectPublicKeyInfo());

        public string Sign(Guid runId, Guid targetAgentId, string hash, DateTimeOffset notAfter) =>
            Convert.ToBase64String(ecdsa.SignData(ApprovalSigning.Payload(ApprovalSigning.KindRun, runId, targetAgentId, hash, notAfter), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        public void Dispose() => ecdsa.Dispose();
    }

    private static Task<HttpResponseMessage> RegisterAsync(TestUser user, Key key, string name = "laptop", string? code = null) =>
        user.PostAsync("/api/me/approval-keys", new { name, publicKey = key.Spki, code });

    private static async Task<RemoteTeam> RequiringTeamAsync(ApiFactory api, bool required = true, string execLevel = ExecLevels.Argv)
    {
        var team = await RemoteKit.TeamAsync(api, execLevel);
        (await team.Target.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(execLevel, false, null, null, 2, ApprovalsRequired: required), TestUser.Json)).EnsureSuccessStatusCode();
        return team;
    }

    private static async Task<(Guid Id, RunCreate Req)> NewRunAsync(RemoteTeam team)
    {
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey());
        return ((await RemoteKit.CreateAsync(team.Requester, req)).Id, req);
    }

    private async Task<HttpResponseMessage> SignedApproveAsync(RemoteTeam team, Guid id, RunCreate req, Key key, string? signatureOverride = null, string? keyIdOverride = null)
    {
        var row = await RemoteKit.RunRowAsync(api, id);
        var hash = RemoteKit.HashOf(req);
        return await team.Owner.PostAsync($"/api/runs/{id}/approve",
            new { hash, keyId = keyIdOverride ?? key.Id, signature = signatureOverride ?? key.Sign(id, team.TargetId, hash, row.ExpiresAt) });
    }

    // ---- the registry -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_person_registers_the_public_half_of_a_key_and_sees_it_listed_with_its_fingerprint()
    {
        var team = await RemoteKit.TeamAsync(api);
        using var key = new Key();
        var response = await RegisterAsync(team.Owner, key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((key.Id, "laptop"), (created.GetProperty("fingerprint").GetString(), created.GetProperty("name").GetString()));

        var list = await team.Owner.GetJsonAsync("/api/me/approval-keys");
        Assert.Equal(key.Id, list[0].GetProperty("fingerprint").GetString());
        Assert.Equal(0, (await team.Other.GetJsonAsync("/api/me/approval-keys")).GetArrayLength()); // nobody else sees it
    }

    [Fact]
    public async Task Something_that_is_not_a_p256_public_key_a_nameless_key_and_the_same_key_twice_are_refused()
    {
        var team = await RemoteKit.TeamAsync(api);
        using var rsa = RSA.Create(2048);
        await RemoteKit.AssertInvalidAsync(await team.Owner.PostAsync("/api/me/approval-keys", new { name = "x", publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()) }), "publicKey", RemoteErrors.InvalidApprovalKey);
        await RemoteKit.AssertInvalidAsync(await team.Owner.PostAsync("/api/me/approval-keys", new { name = "x", publicKey = "%%%" }), "publicKey", RemoteErrors.InvalidApprovalKey);
        using var key = new Key();
        Assert.Equal(HttpStatusCode.BadRequest, (await team.Owner.PostAsync("/api/me/approval-keys", new { name = "", publicKey = key.Spki })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(team.Owner, key)).StatusCode);
        await RemoteKit.AssertProblemAsync(await RegisterAsync(team.Owner, key), HttpStatusCode.Conflict, "approval_key_exists");
    }

    [Fact]
    public async Task At_most_ten_keys_are_live_and_retiring_one_makes_room_and_only_its_owner_can_retire_it()
    {
        var team = await RemoteKit.TeamAsync(api);
        var keys = Enumerable.Range(0, ApprovalKeyEndpoints.ActiveMax).Select(_ => new Key()).ToList();
        try
        {
            foreach (var key in keys) Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(team.Owner, key)).StatusCode);
            using var extra = new Key();
            await RemoteKit.AssertProblemAsync(await RegisterAsync(team.Owner, extra), HttpStatusCode.Conflict, RemoteErrors.ApprovalKeyLimit);

            var first = (await team.Owner.GetJsonAsync("/api/me/approval-keys")).EnumerateArray().First(k => k.GetProperty("fingerprint").GetString() == keys[0].Id).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.NotFound, (await team.Other.PostAsync($"/api/me/approval-keys/{first}/retire")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/me/approval-keys/{first}/retire")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await team.Owner.PostAsync($"/api/me/approval-keys/{first}/retire")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(team.Owner, extra)).StatusCode);
        }
        finally
        {
            foreach (var key in keys) key.Dispose();
        }
    }

    [Fact]
    public async Task An_old_sign_in_needs_a_code_to_register_a_key_like_every_approval_of_consequence()
    {
        var team = await RemoteKit.TeamAsync(api);
        var secret = await RemoteKit.EnableTotpAsync(api, team.Owner);
        api.Clock.Advance(TimeSpan.FromMinutes(11));
        using var key = new Key();
        await RemoteKit.AssertProblemAsync(await RegisterAsync(team.Owner, key), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(team.Owner, key, code: RemoteKit.TotpNow(api, secret))).StatusCode);
    }

    // ---- a signed approval ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_signature_made_with_a_registered_key_is_stored_with_the_run_and_sent_to_the_target()
    {
        var team = await RequiringTeamAsync(api);
        using var key = new Key();
        await RegisterAsync(team.Owner, key);
        var (id, req) = await NewRunAsync(team);

        Assert.Equal(HttpStatusCode.NoContent, (await SignedApproveAsync(team, id, req, key)).StatusCode);

        var row = await RemoteKit.RunRowAsync(api, id);
        Assert.Equal(RunStatuses.Approved, row.Status);
        Assert.Equal(key.Id, row.ApprovalKeyId);
        Assert.NotNull(row.ApprovalSignature);
        Assert.NotNull(row.ApprovalNotAfter);

        await using var scope = api.Services.CreateAsyncScope();
        var waiting = await RunNotices.WaitingForAsync(scope.ServiceProvider.GetRequiredService<MonitorDb>(), team.TargetId, api.Clock.GetUtcNow(), CancellationToken.None, api.Services.GetRequiredService<Broker>());
        var message = Assert.Single(waiting);
        Assert.Equal((key.Id, row.ApprovalSignature), (message.ApprovalKeyId, message.ApprovalSignature));
        Assert.Equal(row.ApprovalNotAfter, message.ApprovalNotAfter);
    }

    [Fact]
    public async Task A_signature_that_does_not_verify_names_an_unknown_key_or_comes_from_a_retired_one_is_refused_and_the_run_stays_pending()
    {
        var team = await RequiringTeamAsync(api);
        using var key = new Key();
        using var stranger = new Key();
        await RegisterAsync(team.Owner, key);
        var (id, req) = await NewRunAsync(team);
        var row = await RemoteKit.RunRowAsync(api, id);

        await RemoteKit.AssertProblemAsync(await SignedApproveAsync(team, id, req, key, signatureOverride: Convert.ToBase64String(new byte[64])), HttpStatusCode.Forbidden, ApprovalErrors.Invalid);
        await RemoteKit.AssertProblemAsync(await SignedApproveAsync(team, id, req, stranger), HttpStatusCode.Forbidden, ApprovalErrors.Invalid); // not registered
        await RemoteKit.AssertProblemAsync(await SignedApproveAsync(team, id, req, key, signatureOverride: key.Sign(Guid.NewGuid(), team.TargetId, RemoteKit.HashOf(req), row.ExpiresAt)), HttpStatusCode.Forbidden, ApprovalErrors.Invalid); // another run
        await RemoteKit.AssertProblemAsync(await SignedApproveAsync(team, id, req, key, signatureOverride: key.Sign(id, Guid.NewGuid(), RemoteKit.HashOf(req), row.ExpiresAt)), HttpStatusCode.Forbidden, ApprovalErrors.Invalid); // another target
        await RemoteKit.AssertProblemAsync(await SignedApproveAsync(team, id, req, key, signatureOverride: key.Sign(id, team.TargetId, RemoteKit.HashOf(req), row.ExpiresAt.AddSeconds(5))), HttpStatusCode.Forbidden, ApprovalErrors.Invalid); // another expiry
        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, id));

        var keyRow = (await team.Owner.GetJsonAsync("/api/me/approval-keys"))[0].GetProperty("id").GetGuid();
        await team.Owner.PostAsync($"/api/me/approval-keys/{keyRow}/retire");
        await RemoteKit.AssertProblemAsync(await SignedApproveAsync(team, id, req, key), HttpStatusCode.Forbidden, ApprovalErrors.Invalid); // retired
    }

    [Fact]
    public async Task Someone_elses_registered_key_does_not_approve_for_the_owner()
    {
        var team = await RequiringTeamAsync(api);
        using var theirs = new Key();
        await RegisterAsync(team.Other, theirs);
        var (id, req) = await NewRunAsync(team);
        await RemoteKit.AssertProblemAsync(await SignedApproveAsync(team, id, req, theirs), HttpStatusCode.Forbidden, ApprovalErrors.Invalid);
    }

    [Fact]
    public async Task A_target_that_requires_signatures_is_not_asked_to_take_an_approval_without_one_and_one_that_does_not_still_may()
    {
        var team = await RequiringTeamAsync(api);
        var (id, req) = await NewRunAsync(team);
        await RemoteKit.AssertProblemAsync(await RemoteKit.ApproveAsync(team.Owner, id, req), HttpStatusCode.Forbidden, ApprovalErrors.Required);
        Assert.Equal(RunStatuses.PendingApproval, await RemoteKit.StatusOfAsync(api, id));

        var plain = await RequiringTeamAsync(api, required: false);
        var (plainId, plainReq) = await NewRunAsync(plain);
        Assert.Equal(HttpStatusCode.NoContent, (await RemoteKit.ApproveAsync(plain.Owner, plainId, plainReq)).StatusCode);
        Assert.Null((await RemoteKit.RunRowAsync(api, plainId)).ApprovalSignature);
    }

    [Fact]
    public async Task The_web_view_tells_the_card_that_the_target_requires_a_signature_and_the_flag_follows_the_next_profile()
    {
        var team = await RequiringTeamAsync(api);
        var (id, _) = await NewRunAsync(team);
        Assert.True((await team.Owner.GetJsonAsync($"/api/runs/{id}")).GetProperty("approvalsRequired").GetBoolean());
        (await team.Target.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(ExecLevels.Argv, false, null, null, 2, ApprovalsRequired: false), TestUser.Json)).EnsureSuccessStatusCode();
        Assert.False((await team.Owner.GetJsonAsync($"/api/runs/{id}")).GetProperty("approvalsRequired").GetBoolean());
    }

    [Fact]
    public async Task A_script_run_is_signed_over_the_hash_that_includes_its_files_stdin_and_role()
    {
        var team = await ScriptKit.TeamAsync(api, files: true);
        (await team.Target.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(ExecLevels.Shell, false, null, null, 2, Interpreters: [ScriptKit.Sh], Tools: [ScriptKit.Sh],
            Capabilities: [AgentCapabilities.Script, AgentCapabilities.ScriptFiles], ApprovalsRequired: true), TestUser.Json)).EnsureSuccessStatusCode();
        using var key = new Key();
        await RegisterAsync(team.Owner, key);
        var req = ScriptKit.Script(team.TargetId, files: [new ScriptFile("data.txt", "x")], stdin: "in");
        var id = (await RemoteKit.CreateAsync(team.Requester, req)).Id;
        var row = await RemoteKit.RunRowAsync(api, id);
        var hash = ScriptKit.HashOf(req, "test", "application");

        var response = await team.Owner.PostAsync($"/api/runs/{id}/approve", new { hash, keyId = key.Id, signature = key.Sign(id, team.TargetId, hash, row.ExpiresAt) });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var scope = api.Services.CreateAsyncScope();
        var message = Assert.Single(await RunNotices.WaitingForAsync(scope.ServiceProvider.GetRequiredService<MonitorDb>(), team.TargetId, api.Clock.GetUtcNow(), CancellationToken.None, api.Services.GetRequiredService<Broker>()));
        Assert.Equal(("test", "application"), (message.TargetEnvironment, message.TargetFunction)); // the agent needs them to rebuild the hash it checks
    }

    // ---- no auto-approval, no schedule ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_target_that_requires_signatures_takes_no_job_and_no_grant_each_run_waits_for_a_signature_of_its_own()
    {
        var team = await RequiringTeamAsync(api);
        var proposed = await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs", new JobProposal(team.TargetId, "Run tests", ["/opt/ci/run-tests"], "/opt/ci", 300, "x"), TestUser.Json);
        var job = (await proposed.Content.ReadFromJsonAsync<JobView>(TestUser.Json))!;
        await team.Owner.PostAsync($"/api/jobs/{job.Id}/approve", new { });
        var viaJob = await RemoteKit.PostRunAsync(team.Requester, RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), ["/opt/ci/run-tests"], "/opt/ci", 120) with { JobId = job.Id });
        var jobRun = (await viaJob.Content.ReadFromJsonAsync<RunCreated>(TestUser.Json))!;
        Assert.Equal(RunStatuses.PendingApproval, jobRun.Status);
        Assert.Null((await RemoteKit.RunRowAsync(api, jobRun.Id)).JobId);

        var grant = await RemoteKit.ActiveGrantAsync(team);
        var viaGrant = await RemoteKit.PostRunAsync(team.Requester, RemoteKit.GrantRun(team.TargetId));
        var grantRun = (await viaGrant.Content.ReadFromJsonAsync<RunCreated>(TestUser.Json))!;
        Assert.Equal(RunStatuses.PendingApproval, grantRun.Status);
        Assert.Null(grantRun.GrantId);
        _ = grant;
    }

    [Fact]
    public async Task The_same_job_and_grant_still_approve_at_once_on_a_target_that_does_not_require_signatures()
    {
        var team = await RequiringTeamAsync(api, required: false);
        await RemoteKit.ActiveGrantAsync(team);
        var viaGrant = await RemoteKit.PostRunAsync(team.Requester, RemoteKit.GrantRun(team.TargetId));
        Assert.Equal(RunStatuses.Approved, (await viaGrant.Content.ReadFromJsonAsync<RunCreated>(TestUser.Json))!.Status);
    }

    [Fact]
    public async Task A_machine_that_requires_signatures_takes_no_schedule_and_one_it_already_had_pauses_with_that_reason()
    {
        var team = await RequiringTeamAsync(api, required: false);
        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/workspaces/{team.Admin.WorkspaceId}/remote-settings", new { remoteSchedulesEnabled = true })).EnsureSuccessStatusCode();
        var proposed = await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs", new JobProposal(team.TargetId, "Run tests", ["/opt/ci/run-tests"], "/opt/ci", 300, "x"), TestUser.Json);
        var job = (await proposed.Content.ReadFromJsonAsync<JobView>(TestUser.Json))!;
        await team.Owner.PostAsync($"/api/jobs/{job.Id}/approve", new { });
        var created = await team.Owner.PostAsync("/api/schedules", new { jobId = job.Id, kind = "interval", intervalMinutes = 15 });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        (await team.Target.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(ExecLevels.Argv, false, null, null, 2, ApprovalsRequired: true), TestUser.Json)).EnsureSuccessStatusCode();
        api.Clock.Advance(TimeSpan.FromMinutes(16));
        await using (var scope = api.Services.CreateAsyncScope())
        {
            await JobSchedules.FireDueAsync(scope.ServiceProvider.GetRequiredService<MonitorDb>(), api.Services.GetRequiredService<ApiConfig>(), api.Services.GetRequiredService<Broker>(), api.Clock.GetUtcNow(), CancellationToken.None);
        }

        await using var db = api.Db();
        var schedule = await db.JobSchedules.AsNoTracking().SingleAsync(s => s.Id == id);
        Assert.Equal((ScheduleStatuses.Paused, ScheduleReasons.Approvals), (schedule.Status, schedule.PauseReason));
        Assert.Equal(0, await db.RemoteRuns.CountAsync(r => r.ScheduleId == id));
        await RemoteKit.AssertProblemAsync(await team.Owner.PostAsync("/api/schedules", new { jobId = job.Id, kind = "interval", intervalMinutes = 30 }), HttpStatusCode.Conflict, RemoteErrors.ScheduleApprovals);
    }
}
