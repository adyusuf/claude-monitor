using Podium.Agent.Mcp;
using Podium.Agent.Storage;
using Podium.Contracts;

namespace Podium.Agent.Tests;

/// <summary>The completeness line of aipod_run_result (ADR-0008 §3d): complete with a size, or exactly what is missing.</summary>
public sealed class ScriptResultTests
{
    private static RunView View(bool? complete, long? read = null, long? dropped = null, long? refused = null, int? missingFrom = null, long? cap = null,
        bool truncated = false, string? error = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "host", RunModes.Script, null, null, null, 30, RunStatuses.Succeeded, 0, error, 100, truncated,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, OutputCap: cap, OutputReadBytes: read, OutputDroppedBytes: dropped,
            OutputRefusedBytes: refused, OutputMissingFromSeq: missingFrom, OutputComplete: complete);

    private static StoredChunk Chunk(int seq, bool gap = false) => new("r", seq, RunStreams.Stdout, "x", gap);

    [Fact]
    public void A_run_with_no_verdict_has_no_line_that_is_every_argv_and_shell_run()
    {
        Assert.Null(ScriptResult.Completeness(null, []));
        Assert.Null(ScriptResult.Completeness(View(complete: null), []));
    }

    [Fact]
    public void A_complete_run_says_complete_and_how_many_bytes()
    {
        Assert.Equal("Output complete, 1500 bytes.", ScriptResult.Completeness(View(true, read: 1500), []));
    }

    [Fact]
    public void Bytes_cut_on_the_target_are_named_with_the_chunk_the_cut_is_before()
    {
        var line = ScriptResult.Completeness(View(false, read: 5000, dropped: 4000), [Chunk(0), Chunk(17, gap: true)]);
        Assert.Equal("Output incomplete: 4000 bytes cut on the target before chunk 17.", line);
    }

    [Fact]
    public void Bytes_refused_by_the_server_cap_say_from_which_chunk_and_under_which_cap()
    {
        var line = ScriptResult.Completeness(View(false, refused: 180_000, missingFrom: 17, cap: 1_048_576, truncated: true), []);
        Assert.Equal("Output incomplete: 180000 bytes from chunk 17 on refused by the server cap of 1048576 bytes.", line);
    }

    [Fact]
    public void Both_kinds_of_loss_and_a_read_limit_stop_are_all_named()
    {
        var line = ScriptResult.Completeness(View(false, dropped: 10, refused: 20, missingFrom: 3, cap: 1_048_576, truncated: true, error: "output_limit"), [Chunk(2, gap: true)])!;
        Assert.Contains("10 bytes cut on the target before chunk 2", line, StringComparison.Ordinal);
        Assert.Contains("20 bytes from chunk 3 on refused", line, StringComparison.Ordinal);
        Assert.Contains("the run was stopped at the read limit", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stop_at_the_read_limit_alone_is_still_named()
    {
        Assert.Contains("read limit", ScriptResult.Completeness(View(false, truncated: true), [])!, StringComparison.Ordinal);
    }
}
