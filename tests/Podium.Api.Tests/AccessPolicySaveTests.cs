using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Podium.Api.AccessPolicies;
using Podium.Api.Data;
using Podium.Api.Endpoints;
using Podium.Api.Remote;
using Podium.Api.Security;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// Saving and reading a project's access policy (ADR-0020 §4 rules 1-3): only the workspace's owners and admins save, re-authenticated, a valid and
/// already canonical document over the project's own repositories, as a compare-and-set on the version; a repository belongs to one live policy; a
/// signature is checked against the saver's registered key for the next version; the audit row holds no path.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class AccessPolicySaveTests(ApiFactory api)
{
    private const string Shop = "git:git.example.invalid/team/shop"; // the repository of ProjectKit.Application
    private static readonly AccessOsRules None = new([], [], []);
    private static readonly TimeSpan PastWindow = TimeSpan.FromMinutes(11); // the re-authentication window is ten minutes

    private static AccessPolicyDocument Doc(string folder = "/srv/app", bool cleared = false) =>
        AccessPolicyRules.Canonical(new AccessPolicyDocument(AccessPolicyRules.Format, [Shop], false, cleared ? None : new AccessOsRules([folder], ["/srv/app/secret"], []),
            None, None, false, cleared));

    private static async Task<Guid> ProjectAsync(TestUser admin, string name)
    {
        var id = (await ProjectKit.CreateProjectAsync(admin, name)).GetProperty("id").GetGuid();
        await ProjectKit.CreateApplicationAsync(admin, id, "Shop");
        return id;
    }

    private static Task<HttpResponseMessage> SaveAsync(TestUser user, Guid projectId, int expected, AccessPolicyDocument document, string? keyId = null,
        string? signature = null) =>
        user.SendAsync(HttpMethod.Put, $"/api/projects/{projectId}/access-policy", new AccessPolicySave(expected, document, keyId, signature, null));

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ---- who may save and read --------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_admin_saves_version_one_and_every_member_reads_it_but_only_the_admin_may_edit()
    {
        var admin = await api.NewClient().SignedUpAsync("aps-admin");
        var member = await RemoteKit.MemberAsync(api, admin, "aps-member");
        var project = await ProjectAsync(admin, "Policy save");

        var saved = await BodyAsync(await SaveAsync(admin, project, 0, Doc()));
        var read = await member.GetJsonAsync($"/api/projects/{project}/access-policy");

        Assert.Equal((1, false, false), (saved.GetProperty("version").GetInt32(), saved.GetProperty("isDefault").GetBoolean(), saved.GetProperty("isSigned").GetBoolean()));
        Assert.Equal(AccessPolicySigning.Hash(Doc()), saved.GetProperty("hash").GetString());
        Assert.Equal((1, false), (read.GetProperty("version").GetInt32(), read.GetProperty("canEdit").GetBoolean()));
        Assert.Equal(["/srv/app"], read.GetProperty("document").GetProperty("linux").GetProperty("allowedFolders").EnumerateArray().Select(e => e.GetString()));
        Assert.True((await admin.GetJsonAsync($"/api/projects/{project}/access-policy")).GetProperty("canEdit").GetBoolean());
    }

    [Fact]
    public async Task A_project_without_a_policy_reads_as_version_zero_with_the_default_over_its_repositories()
    {
        var admin = await api.NewClient().SignedUpAsync("aps-default");
        var project = await ProjectAsync(admin, "Policy default");

        var read = await admin.GetJsonAsync($"/api/projects/{project}/access-policy");

        Assert.Equal((0, true), (read.GetProperty("version").GetInt32(), read.GetProperty("isDefault").GetBoolean()));
        Assert.Equal([Shop], read.GetProperty("candidateKeys").EnumerateArray().Select(e => e.GetString()));
        Assert.True(read.GetProperty("document").GetProperty("includeProjectFolder").GetBoolean());
    }

    [Theory]
    [InlineData("member")]
    [InlineData("viewer")]
    public async Task A_member_or_viewer_who_is_not_an_admin_cannot_save(string role)
    {
        var admin = await api.NewClient().SignedUpAsync("aps-role");
        var user = await RemoteKit.MemberAsync(api, admin, "aps-" + role, role);
        var project = await ProjectAsync(admin, "Policy role " + role);

        Assert.Equal(HttpStatusCode.Forbidden, (await SaveAsync(user, project, 0, Doc())).StatusCode);
        await AssertNoPolicyAsync(project);
    }

    [Fact]
    public async Task Someone_outside_the_workspace_gets_not_found_for_reading_and_saving()
    {
        var admin = await api.NewClient().SignedUpAsync("aps-owner");
        var stranger = await api.NewClient().SignedUpAsync("aps-stranger");
        var project = await ProjectAsync(admin, "Policy hidden");

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/projects/{project}/access-policy")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SaveAsync(stranger, project, 0, Doc())).StatusCode);
        await AssertNoPolicyAsync(project);
    }

    [Fact]
    public async Task A_sign_in_older_than_the_window_cannot_save()
    {
        var admin = await api.NewClient().SignedUpAsync("aps-stale");
        var project = await ProjectAsync(admin, "Policy stale");
        api.Clock.Advance(PastWindow);

        await RemoteKit.AssertProblemAsync(await SaveAsync(admin, project, 0, Doc()), HttpStatusCode.Forbidden, RemoteChecks.ReauthRequired);
        await AssertNoPolicyAsync(project);
    }

    // ---- what is accepted -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_document_that_is_invalid_not_canonical_or_names_another_repository_is_refused_with_its_code()
    {
        var admin = await api.NewClient().SignedUpAsync("aps-invalid");
        var project = await ProjectAsync(admin, "Policy invalid");
        var unsorted = Doc() with { Linux = new AccessOsRules(["/srv/b", "/srv/a"], [], []) };
        var forbidden = Doc() with { Linux = new AccessOsRules(["/"], [], []) };
        var foreign = AccessPolicyRules.Canonical(Doc() with { ProjectKeys = ["git:git.example.invalid/team/other"] });
        var twice = AccessPolicyRules.Canonical(Doc() with { ProjectKeys = [Shop, "git:git.example.invalid/Team/Shop"] });

        await RemoteKit.AssertInvalidAsync(await SaveAsync(admin, project, 0, unsorted), "document", AccessPolicyErrors.NotCanonical);
        await RemoteKit.AssertInvalidAsync(await SaveAsync(admin, project, 0, forbidden), "document", AccessPolicyErrors.ForbiddenRoot);
        await RemoteKit.AssertInvalidAsync(await SaveAsync(admin, project, 0, foreign), "document", AccessPolicyErrors.Keys);
        await RemoteKit.AssertInvalidAsync(await SaveAsync(admin, project, 0, twice), "document", AccessPolicyErrors.Keys);
        await RemoteKit.AssertInvalidAsync(await SaveAsync(admin, project, -1, Doc()), "expectedVersion", AccessPolicySaves.InvalidVersion);
        await AssertNoPolicyAsync(project);
    }

    [Fact]
    public async Task A_second_save_from_the_same_version_is_refused_as_changed()
    {
        var admin = await api.NewClient().SignedUpAsync("aps-cas");
        var project = await ProjectAsync(admin, "Policy cas");
        await BodyAsync(await SaveAsync(admin, project, 0, Doc()));

        await RemoteKit.AssertProblemAsync(await SaveAsync(admin, project, 0, Doc("/srv/other")), HttpStatusCode.Conflict, AccessPolicyErrors.Changed);
        var next = await BodyAsync(await SaveAsync(admin, project, 1, Doc("/srv/other")));

        Assert.Equal(2, next.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Two_first_saves_at_once_store_one_version_and_refuse_the_other()
    {
        var admin = await api.NewClient().SignedUpAsync("aps-race");
        var project = await ProjectAsync(admin, "Policy race");

        var answers = await Task.WhenAll(SaveAsync(admin, project, 0, Doc("/srv/one")), SaveAsync(admin, project, 0, Doc("/srv/two")));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], answers.Select(a => a.StatusCode).Order());
        await using var db = api.Db();
        Assert.Equal(1, (await db.ProjectAccessPolicies.AsNoTracking().SingleAsync(p => p.ProjectId == project)).Version);
    }

    // ---- one repository, one live policy ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_repository_held_by_another_projects_live_policy_is_refused_and_passes_once_that_policy_is_taken_back()
    {
        var admin = await api.NewClient().SignedUpAsync("aps-keys");
        var first = await ProjectAsync(admin, "Policy first");
        var second = await ProjectAsync(admin, "Policy second");
        await BodyAsync(await SaveAsync(admin, first, 0, Doc()));

        await RemoteKit.AssertProblemAsync(await SaveAsync(admin, second, 0, Doc()), HttpStatusCode.Conflict, AccessPolicyErrors.KeyTaken);
        await BodyAsync(await SaveAsync(admin, first, 1, Doc(cleared: true)));
        await BodyAsync(await SaveAsync(admin, second, 0, Doc()));

        var byKey = await admin.GetJsonAsync($"/api/workspaces/{admin.WorkspaceId}/access-policies/by-key?projectKey={Uri.EscapeDataString(Shop)}");
        Assert.Equal(second, byKey.GetProperty("projectId").GetGuid());
        await using var db = api.Db();
        Assert.Equal([second], await db.ProjectAccessPolicyKeys.AsNoTracking().Where(k => k.WorkspaceId == admin.WorkspaceId)
            .Join(db.ProjectAccessPolicies, k => k.PolicyId, p => p.Id, (k, p) => p.ProjectId).ToListAsync());
    }

    // ---- signatures -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_signature_over_the_next_version_with_the_savers_registered_key_is_kept_and_any_other_is_refused()
    {
        var admin = await api.NewClient().SignedUpAsync("aps-signed", "Signing Admin");
        var project = await ProjectAsync(admin, "Policy signed");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var unregistered = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        (await admin.PostAsync("/api/me/approval-keys", new { name = "laptop", publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) })).EnsureSuccessStatusCode();
        var keyId = ApprovalSigning.Fingerprint(key.ExportSubjectPublicKeyInfo());
        var hash = AccessPolicySigning.Hash(Doc());
        string Sign(ECDsa signer, int version) => Convert.ToBase64String(signer.SignData(AccessPolicySigning.Payload(admin.WorkspaceId, project, version, hash),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        await RemoteKit.AssertProblemAsync(await SaveAsync(admin, project, 0, Doc(), keyId, Sign(key, 0)), HttpStatusCode.Forbidden, ApprovalErrors.Invalid);
        await RemoteKit.AssertProblemAsync(await SaveAsync(admin, project, 0, Doc(), keyId, Sign(unregistered, 1)), HttpStatusCode.Forbidden, ApprovalErrors.Invalid);
        await RemoteKit.AssertProblemAsync(await SaveAsync(admin, project, 0, Doc(),
            ApprovalSigning.Fingerprint(unregistered.ExportSubjectPublicKeyInfo()), Sign(unregistered, 1)), HttpStatusCode.Forbidden, ApprovalErrors.Invalid);
        await AssertNoPolicyAsync(project);

        var saved = await BodyAsync(await SaveAsync(admin, project, 0, Doc(), keyId, Sign(key, 1)));

        Assert.Equal((true, "Signing Admin"), (saved.GetProperty("isSigned").GetBoolean(), saved.GetProperty("signerName").GetString()));
        var delivered = await DeliveredAsync(admin, project);
        Assert.Equal((keyId, Sign(key, 1).Length), (delivered.KeyId, delivered.Signature!.Length));
        Assert.True(AccessPolicySigning.Verify(key.ExportSubjectPublicKeyInfo(), admin.WorkspaceId, project, delivered.Version, delivered.Hash,
            Convert.FromBase64String(delivered.Signature)));
    }

    [Fact]
    public async Task The_audit_row_counts_what_changed_and_holds_no_path()
    {
        var admin = await api.NewClient().SignedUpAsync("aps-audit");
        var project = await ProjectAsync(admin, "Policy audit");

        await BodyAsync(await SaveAsync(admin, project, 0, Doc("/srv/very-private-folder")));
        await BodyAsync(await SaveAsync(admin, project, 1, Doc(cleared: true)));

        await using var db = api.Db();
        var rows = await db.AuditEvents.AsNoTracking().Where(a => a.WorkspaceId == admin.WorkspaceId && a.TargetId == project.ToString()
            && (a.Action == AuditActions.AccessPolicyUpdated || a.Action == AuditActions.AccessPolicyCleared)).OrderBy(a => a.Id).ToListAsync();
        Assert.Equal([AuditActions.AccessPolicyUpdated, AuditActions.AccessPolicyCleared], rows.Select(r => r.Action));
        var detail = rows[0].Detail!.RootElement;
        Assert.Equal((1, 1), (detail.GetProperty("allowedFolders").GetInt32(), detail.GetProperty("deniedFolders").GetInt32()));
        Assert.DoesNotContain("/srv", rows[0].Detail!.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Shop, rows[0].Detail!.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    // ---- the session page's line ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_live_policy_of_a_repository_is_found_in_any_case_for_members_only_and_a_cleared_one_is_not()
    {
        var admin = await api.NewClient().SignedUpAsync("aps-bykey");
        var viewer = await RemoteKit.MemberAsync(api, admin, "aps-bykey-viewer", "viewer");
        var stranger = await api.NewClient().SignedUpAsync("aps-bykey-out");
        var project = await ProjectAsync(admin, "Policy by key");
        await BodyAsync(await SaveAsync(admin, project, 0, Doc()));
        string Url(string key) => $"/api/workspaces/{admin.WorkspaceId}/access-policies/by-key?projectKey={Uri.EscapeDataString(key)}";

        var found = await viewer.GetJsonAsync(Url("git:git.example.invalid/TEAM/shop"));

        Assert.Equal((project, 1, false), (found.GetProperty("projectId").GetGuid(), found.GetProperty("version").GetInt32(), found.GetProperty("enforced").GetBoolean()));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync(Url(Shop))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Http.GetAsync(Url("git:git.example.invalid/team/other"))).StatusCode);
        await RemoteKit.AssertInvalidAsync(await admin.Http.GetAsync(Url("git:bad‮key")), "projectKey", AccessPolicyEndpoints.InvalidKey);
        await BodyAsync(await SaveAsync(admin, project, 1, Doc(cleared: true)));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Http.GetAsync(Url(Shop))).StatusCode);
    }

    private async Task AssertNoPolicyAsync(Guid projectId)
    {
        await using var db = api.Db();
        Assert.False(await db.ProjectAccessPolicies.AnyAsync(p => p.ProjectId == projectId));
    }

    private static async Task<AccessPolicyMessage> DeliveredAsync(TestUser admin, Guid projectId)
    {
        var agent = await admin.ConnectAgentAsync();
        var set = await agent.Http.GetFromJsonAsync<AgentAccessPolicies>("/api/agent/access-policies", TestUser.Json);
        return set!.Policies.Single(p => p.ProjectId == projectId);
    }
}
