using System.Net;
using System.Net.Http.Json;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>
/// Who reads a job's command (F-151, kept as designed and documented in ADR-0005 "Jobs"): every member of the workspace, viewers included, sees a job's argv
/// and working folder, while a grant is shown only to the machine's owner and its grantee. A job is a shared, named command; the argv is not a place for a secret.
/// </summary>
[Collection(ApiGroup.Name)]
public sealed class JobVisibilityTests(ApiFactory api)
{
    private static readonly string[] Argv = ["/opt/ci/run-tests", "--suite", "unit"];

    [Fact]
    public async Task Every_member_including_a_viewer_sees_a_jobs_argv_and_cwd_and_so_does_an_agent_of_the_workspace()
    {
        var team = await RemoteKit.TeamAsync(api);
        var viewer = await RemoteKit.MemberAsync(api, team.Admin, "jv-viewer", "viewer");
        var response = await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs", new JobProposal(team.TargetId, "Run tests", Argv, "/opt/ci", 300, "needs a test run"), TestUser.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<JobView>(TestUser.Json))!;
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.PostAsync($"/api/jobs/{job.Id}/approve", new { })).StatusCode); // active, and a second one still proposed below
        Assert.Equal(HttpStatusCode.OK, (await team.Requester.Http.PostAsJsonAsync("/api/agent/jobs",
            new JobProposal(team.TargetId, "Run lint", ["/opt/ci/run-tests", "--suite", "lint"], "/opt/ci/lint", 300, null), TestUser.Json)).StatusCode);

        foreach (var reader in new[] { team.Owner, team.Admin, team.Other, viewer })
        {
            var rows = (await reader.GetJsonAsync($"/api/agents/{team.TargetId}/jobs")).EnumerateArray().ToList();
            Assert.Equal(2, rows.Count);
            var active = rows.Single(r => r.GetProperty("name").GetString() == "Run tests");
            Assert.Equal(Argv, active.GetProperty("argv").EnumerateArray().Select(a => a.GetString()).ToArray());
            Assert.Equal("/opt/ci", active.GetProperty("cwd").GetString());
            var proposed = rows.Single(r => r.GetProperty("name").GetString() == "Run lint");
            Assert.Equal("/opt/ci/lint", proposed.GetProperty("cwd").GetString());
            Assert.Equal(3, proposed.GetProperty("argv").GetArrayLength());
        }

        var listed = await team.Requester.Http.GetFromJsonAsync<List<JobView>>($"/api/agent/jobs?target={team.TargetId}", TestUser.Json);
        Assert.Contains(listed!, j => j.Argv.SequenceEqual(Argv) && j.Cwd == "/opt/ci");
    }

    [Fact]
    public async Task A_grant_unlike_a_job_is_shown_only_to_the_owner_and_its_grantee()
    {
        var team = await RemoteKit.TeamAsync(api);
        var viewer = await RemoteKit.MemberAsync(api, team.Admin, "jv-viewer", "viewer");
        await RemoteKit.RequestGrantAsync(team.Requester, RemoteKit.GrantAsk(team.TargetId));

        Assert.Single((await team.Owner.GetJsonAsync($"/api/agents/{team.TargetId}/grants")).EnumerateArray());
        foreach (var reader in new[] { team.Other, viewer })
        {
            Assert.Empty((await reader.GetJsonAsync($"/api/agents/{team.TargetId}/grants")).EnumerateArray());
        }
    }
}
