using System.Net;
using Podium.Api.Data;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Tests;

/// <summary>A target that refuses a run on its own access layers says so with an <c>access_</c> code (ADR-0020 §4): the layer and version are kept, the refusal is audited, and what the API does not know is not stored.</summary>
[Collection(ApiGroup.Name)]
public sealed class RunAccessRefusalTests(ApiFactory api)
{
    private async Task<(RemoteTeam Team, Guid Id)> RunningAsync()
    {
        var team = await RemoteKit.TeamAsync(api);
        await using (var db = api.Db())
        {
            // A run keeps a repository only when the machine reported it (RunCreator.TargetKeyAsync).
            db.MachineProjects.Add(new MachineProject { AgentId = team.TargetId, ProjectKey = "shop-repo", WorkspaceId = team.WorkspaceId, Name = "shop", ClaudeTrusted = false });
            await db.SaveChangesAsync();
        }

        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey(), cwd: "/tmp") with { ProjectKey = "shop-repo" };
        var id = (await RemoteKit.CreateAsync(team.Requester, req)).Id;
        (await RemoteKit.ApproveAsync(team.Owner, id, req)).EnsureSuccessStatusCode();
        (await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate("running"))).EnsureSuccessStatusCode();
        return (team, id);
    }

    private async Task<List<AuditEvent>> RefusalsAsync(Guid id)
    {
        await using var db = api.Db();
        return await db.AuditEvents.AsNoTracking().Where(a => a.Action == "run.access_refused" && a.TargetId == id.ToString()).ToListAsync();
    }

    [Fact]
    public async Task A_refusal_by_a_layer_is_audited_with_the_code_the_layer_the_version_and_the_repository()
    {
        var (team, id) = await RunningAsync();

        var response = await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate("failed", Error: AccessErrors.PathDenied, RefusedBy: AccessRefusedBy.Policy, AccessVersion: 3));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var audit = Assert.Single(await RefusalsAsync(id));
        var detail = audit.Detail!.RootElement;
        Assert.Equal((team.TargetId, team.WorkspaceId), (audit.ActorAgentId, audit.WorkspaceId));
        Assert.Equal((AccessErrors.PathDenied, AccessRefusedBy.Policy, 3, "shop-repo"),
            (detail.GetProperty("code").GetString(), detail.GetProperty("refusedBy").GetString(), detail.GetProperty("version").GetInt32(), detail.GetProperty("projectKey").GetString()));
        Assert.Equal(id, detail.GetProperty("runId").GetGuid());
    }

    [Fact]
    public async Task The_layer_and_version_are_kept_on_the_run_for_the_web_to_show_with_the_error()
    {
        var (team, id) = await RunningAsync();

        await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate("failed", Error: AccessErrors.ExeNotAllowed, RefusedBy: AccessRefusedBy.Ceiling, AccessVersion: 7));

        var row = await RemoteKit.RunRowAsync(api, id);
        Assert.Equal((AccessRefusedBy.Ceiling, 7), (row.RefusedBy, row.AccessVersion));
    }

    [Fact]
    public async Task A_layer_the_api_does_not_know_and_a_version_below_one_are_not_stored()
    {
        var (team, id) = await RunningAsync();

        await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate("failed", Error: AccessErrors.CwdDenied, RefusedBy: "something-new", AccessVersion: 0));

        var row = await RemoteKit.RunRowAsync(api, id);
        Assert.Null(row.RefusedBy);
        Assert.Null(row.AccessVersion);
        var detail = Assert.Single(await RefusalsAsync(id)).Detail!.RootElement;
        Assert.Equal(System.Text.Json.JsonValueKind.Null, detail.GetProperty("refusedBy").ValueKind);
    }

    [Theory]
    [InlineData("access_new_code_from_a_newer_agent", "access_new_code_from_a_newer_agent")]
    [InlineData("access_Not Plain!", "access_other")]
    public async Task A_code_the_api_does_not_know_is_kept_only_when_it_is_a_plain_code(string sent, string audited)
    {
        var (team, id) = await RunningAsync();

        await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate("failed", Error: sent));

        var detail = Assert.Single(await RefusalsAsync(id)).Detail!.RootElement;
        Assert.Equal(audited, detail.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_failure_that_is_not_an_access_refusal_is_not_audited_as_one_and_keeps_no_layer()
    {
        var (team, id) = await RunningAsync();

        await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate("failed", 3, "exit 3", RefusedBy: AccessRefusedBy.Policy, AccessVersion: 2));

        Assert.Empty(await RefusalsAsync(id));
        var row = await RemoteKit.RunRowAsync(api, id);
        Assert.Null(row.RefusedBy);
        Assert.Null(row.AccessVersion);
    }
}
