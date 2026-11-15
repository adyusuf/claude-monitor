using System.Net.Http.Json;
using Podium.Api.AccessPolicies;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>
/// The access report a machine sends with its profile (ADR-0020 §4) is text from the machine: every list is capped, a path that is empty, too long or
/// holds a hidden character is dropped, a SHA-256 that is not lowercase hex is dropped, a policy entry in an unknown state is dropped, and a stored
/// report the API cannot read counts as none.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class AccessReportsTests(ApiFactory api)
{
    private static readonly string Sha = new('a', 64);

    private async Task<AccessReport?> SendAsync(AccessReport report)
    {
        var admin = await api.NewClient().SignedUpAsync("arep");
        var agent = await admin.ConnectAgentAsync();
        (await agent.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(ExecLevels.Argv, false, null, null, 2, Access: report), TestUser.Json))
            .EnsureSuccessStatusCode();
        await using var db = api.Db();
        return AccessReports.Read((await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agent.Tokens.AgentId)).AccessReport);
    }

    [Fact]
    public async Task Ceiling_paths_are_capped_deduplicated_and_cleaned_of_empty_overlong_and_hidden_entries()
    {
        List<string> allowed = ["", "/srv/a‮b", "/srv/zero​width", "/srv/bell\u0007", "/" + new string('a', AccessPolicyRules.MaxPathLength), "/srv/dup", "/srv/dup",
            .. Enumerable.Range(0, 70).Select(i => $"/srv/f{i:D2}")];

        var stored = await SendAsync(new AccessReport(new AccessCeilingReport(allowed, ["/srv/x‮"], [], true), []));

        Assert.Equal(AccessReports.CeilingMax, stored!.Ceiling.AllowedFolders.Count);
        Assert.Equal(["/srv/dup", .. Enumerable.Range(0, 63).Select(i => $"/srv/f{i:D2}")], stored.Ceiling.AllowedFolders);
        Assert.Empty(stored.Ceiling.DeniedFolders);
        Assert.True(stored.Ceiling.Shell);
    }

    [Fact]
    public async Task Ceiling_applications_keep_only_a_lowercase_hex_sha256_and_drop_a_hidden_path()
    {
        AccessApp[] apps = [new("/usr/bin/tail", Sha), new("/usr/bin/head", Sha.ToUpperInvariant()), new("/usr/bin/cat", "not-a-hash"), new("/usr/bin/t‮ail", Sha)];

        var stored = await SendAsync(new AccessReport(new AccessCeilingReport([], [], apps, false), []));

        Assert.Equal([new AccessApp("/usr/bin/tail", Sha), new AccessApp("/usr/bin/head", null), new AccessApp("/usr/bin/cat", null)], stored!.Ceiling.Applications);
    }

    [Fact]
    public async Task Policy_entries_in_an_unknown_state_are_dropped_the_rest_capped_at_two_hundred_and_their_fields_clamped()
    {
        var known = Guid.NewGuid();
        List<AccessAppliedReport> policies = [new(Guid.NewGuid(), 1, "widened", false, null), new(known, -3, AccessPolicyStates.Applied, true, "key‮id", -1),
            new(Guid.NewGuid(), 2, AccessPolicyStates.KeptSigned, true, new string('k', AccessReports.KeyIdMax + 1), AccessReports.ProjectFoldersMax + 5),
            .. Enumerable.Range(0, 205).Select(_ => new AccessAppliedReport(Guid.NewGuid(), 1, AccessPolicyStates.Default, false, "abc"))];

        var stored = await SendAsync(new AccessReport(new AccessCeilingReport([], [], [], false), policies));

        Assert.Equal(AccessPolicyDelivery.PoliciesMax, stored!.Policies.Count);
        Assert.DoesNotContain(stored.Policies, p => p.State == "widened");
        Assert.Equal(new AccessAppliedReport(known, 0, AccessPolicyStates.Applied, true, null, 0), stored.Policies[0]);
        Assert.Equal((null, AccessReports.ProjectFoldersMax), (stored.Policies[1].KeyId, stored.Policies[1].ProjectFolders));
        Assert.Equal("abc", stored.Policies[2].KeyId);
    }

    [Fact]
    public async Task A_profile_without_a_report_stores_none_and_a_stored_report_that_cannot_be_read_counts_as_none()
    {
        var admin = await api.NewClient().SignedUpAsync("arep-none");
        var agent = await admin.ConnectAgentAsync();
        (await agent.Http.PutAsJsonAsync("/api/agent/profile", new AgentProfile(ExecLevels.Argv, false, null, null, 2), TestUser.Json)).EnsureSuccessStatusCode();
        await using var db = api.Db();

        Assert.Null((await db.Agents.AsNoTracking().SingleAsync(a => a.Id == agent.Tokens.AgentId)).AccessReport);
        Assert.Null(AccessReports.Read("{ not json"));
        Assert.Null(AccessReports.Read(null));
    }
}
