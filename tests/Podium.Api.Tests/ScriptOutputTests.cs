using System.Net;
using System.Net.Http.Json;
using Podium.Api.Tests.Infrastructure;
using Podium.Contracts;

namespace Podium.Api.Tests;

/// <summary>A script run's output is complete or the result says what is missing (ADR-0008 §3d): the cap is frozen on the run, a refusal is one range, the verdict is stored.</summary>
[Collection(ApiGroup.Name)]
public sealed class ScriptOutputTests(ApiFactory api)
{
    private const int Mib = 1024 * 1024;

    private static async Task SetCapAsync(RemoteTeam team, long bytes) =>
        (await team.Admin.SendAsync(HttpMethod.Put, $"/api/workspaces/{team.WorkspaceId}/remote-settings", new { scriptOutputMaxBytes = bytes })).EnsureSuccessStatusCode();

    /// <summary>A script run that the owner approved and the target started.</summary>
    private static async Task<Guid> RunningAsync(RemoteTeam team)
    {
        var req = ScriptKit.Script(team.TargetId);
        var created = (await (await RemoteKit.PostRunAsync(team.Requester, req)).Content.ReadFromJsonAsync<RunCreated>(TestUser.Json))!;
        (await ScriptKit.ApproveAsync(team.Owner, created.Id, req, "test", "application")).EnsureSuccessStatusCode();
        (await RemoteKit.StatusAsync(team.Target, created.Id, new RunStatusUpdate(RunStatuses.Running))).EnsureSuccessStatusCode();
        return created.Id;
    }

    [Fact]
    public async Task The_workspace_cap_is_set_by_an_admin_within_one_and_sixty_four_mebibytes_and_frozen_on_each_new_run()
    {
        var team = await ScriptKit.TeamAsync(api);
        await SetCapAsync(team, 2 * Mib);
        var first = await RunningAsync(team);
        await SetCapAsync(team, 4 * Mib);

        Assert.Equal(2L * Mib, (await RemoteKit.RunRowAsync(api, first)).OutputCap);
        Assert.Equal(4L * Mib, (await RemoteKit.RunRowAsync(api, await RunningAsync(team))).OutputCap);
        foreach (var bad in new long[] { Mib - 1, 64L * Mib + 1, 0, -5 })
        {
            var response = await team.Admin.SendAsync(HttpMethod.Put, $"/api/workspaces/{team.WorkspaceId}/remote-settings", new { scriptOutputMaxBytes = bad });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await team.Owner.SendAsync(HttpMethod.Put, $"/api/workspaces/{team.WorkspaceId}/remote-settings", new { scriptOutputMaxBytes = Mib })).StatusCode);
    }

    [Fact]
    public async Task Every_chunk_after_the_first_one_over_the_cap_is_refused_even_a_small_one_so_the_missing_part_is_one_range()
    {
        var team = await ScriptKit.TeamAsync(api);
        await SetCapAsync(team, Mib);
        var id = await RunningAsync(team);

        const int size = 60_000;
        for (var from = 0; from < 20; from += 10)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await ScriptKit.ChunksAsync(team.Target, id, [.. Enumerable.Range(from, 10).Select(s => ScriptKit.Chunk(s, size))])).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await ScriptKit.ChunksAsync(team.Target, id, ScriptKit.Chunk(20, 10))).StatusCode); // would fit, still refused

        var row = await RemoteKit.RunRowAsync(api, id);
        Assert.Equal(17, row.OutputMissingFromSeq); // 17 chunks of 60 000 bytes are 1 020 000 bytes; the 18th does not fit under 1 MiB
        Assert.Equal(3L * size + 10, row.OutputRefusedBytes);
        Assert.Equal(17L * size, row.OutputBytes);
        Assert.True(row.OutputTruncated);
    }

    [Fact]
    public async Task The_final_status_stores_what_the_target_read_dropped_and_sent_and_the_verdict_is_incomplete_when_the_server_refused_anything()
    {
        var team = await ScriptKit.TeamAsync(api);
        await SetCapAsync(team, Mib);
        var id = await RunningAsync(team);
        for (var from = 0; from < 20; from += 10)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await ScriptKit.ChunksAsync(team.Target, id, [.. Enumerable.Range(from, 10).Select(s => ScriptKit.Chunk(s, 60_000))])).StatusCode);
        }

        (await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate(RunStatuses.Succeeded, 0, OutputReadBytes: 1_200_000, OutputDroppedBytes: 0, OutputChunks: 20))).EnsureSuccessStatusCode();

        var row = await RemoteKit.RunRowAsync(api, id);
        Assert.Equal((1_200_000L, 0L, 20), (row.OutputReadBytes, row.OutputDroppedBytes, row.OutputChunks));
        Assert.False(row.OutputComplete);
        Assert.True(row.OutputRefusedBytes > 0);
    }

    [Fact]
    public async Task Output_that_fits_and_lost_nothing_on_the_target_is_complete()
    {
        var team = await ScriptKit.TeamAsync(api);
        var id = await RunningAsync(team);
        await ScriptKit.ChunksAsync(team.Target, id, ScriptKit.Chunk(0, 1000), ScriptKit.Chunk(1, 500));

        (await RemoteKit.StatusAsync(team.Target, id, new RunStatusUpdate(RunStatuses.Succeeded, 0, OutputReadBytes: 1500, OutputDroppedBytes: 0, OutputChunks: 2))).EnsureSuccessStatusCode();

        var row = await RemoteKit.RunRowAsync(api, id);
        Assert.True(row.OutputComplete);
        Assert.Null(row.OutputMissingFromSeq);
        Assert.Null(row.OutputRefusedBytes);
    }

    [Fact]
    public async Task Bytes_dropped_on_the_target_or_a_read_limit_kill_make_the_verdict_incomplete_even_when_the_server_took_everything_it_got()
    {
        var team = await ScriptKit.TeamAsync(api);
        var dropped = await RunningAsync(team);
        await ScriptKit.ChunksAsync(team.Target, dropped, ScriptKit.Chunk(0, 1000));
        (await RemoteKit.StatusAsync(team.Target, dropped, new RunStatusUpdate(RunStatuses.Succeeded, 0, OutputReadBytes: 5000, OutputDroppedBytes: 4000, OutputChunks: 1))).EnsureSuccessStatusCode();
        Assert.False((await RemoteKit.RunRowAsync(api, dropped)).OutputComplete);

        var killed = await RunningAsync(team);
        await ScriptKit.ChunksAsync(team.Target, killed, ScriptKit.Chunk(0, 1000));
        (await RemoteKit.StatusAsync(team.Target, killed, new RunStatusUpdate(RunStatuses.Failed, null, "read limit", OutputTruncated: true, OutputReadBytes: 1000, OutputDroppedBytes: 0, OutputChunks: 1))).EnsureSuccessStatusCode();
        Assert.False((await RemoteKit.RunRowAsync(api, killed)).OutputComplete);
    }

    [Fact]
    public async Task An_argv_run_keeps_its_own_limit_and_never_gets_a_completeness_verdict()
    {
        var team = await RemoteKit.TeamAsync(api);
        var req = RemoteKit.Argv(team.TargetId, RemoteKit.NewKey());
        var created = await RemoteKit.CreateAsync(team.Requester, req);
        (await RemoteKit.ApproveAsync(team.Owner, created.Id, req)).EnsureSuccessStatusCode();
        (await RemoteKit.StatusAsync(team.Target, created.Id, new RunStatusUpdate(RunStatuses.Running))).EnsureSuccessStatusCode();
        await ScriptKit.ChunksAsync(team.Target, created.Id, ScriptKit.Chunk(0, 100));
        (await RemoteKit.StatusAsync(team.Target, created.Id, new RunStatusUpdate(RunStatuses.Succeeded, 0, OutputReadBytes: 100, OutputChunks: 1))).EnsureSuccessStatusCode();

        var row = await RemoteKit.RunRowAsync(api, created.Id);
        Assert.Null(row.OutputCap);
        Assert.Null(row.OutputComplete);
        Assert.Null(row.OutputReadBytes);
    }
}
