using System.Text;
using ClaudeMonitor.Agent.Exec;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Tests;

public sealed class OutputCollectorTests
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private const string Stdout = RunStreams.Stdout;
    private const string Stderr = RunStreams.Stderr;

    private static OutputCollector Big() => new(1 << 20, 1 << 20, 32 * 1024, 1L << 30);

    private static List<RunOutputChunk> Drain(OutputCollector collector)
    {
        var chunks = new List<RunOutputChunk>();
        while (collector.Chunks.TryRead(out var chunk)) chunks.Add(chunk);
        return chunks;
    }

    private static string Text(IEnumerable<RunOutputChunk> chunks) => string.Concat(chunks.Select(c => c.Body));

    private static string Run(byte[] input, int step = int.MaxValue)
    {
        var collector = Big();
        for (var i = 0; i < input.Length; i += step) collector.Add(Stdout, input.AsSpan(i, Math.Min(step, input.Length - i)));
        collector.Complete();
        return Text(Drain(collector));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1)]
    [InlineData(3)]
    public void ANSI_colours_cursor_moves_and_window_titles_are_removed_even_when_split_between_reads(int step)
    {
        var input = "\u001b[31mred\u001b[0m \u001b[2K\u001b[1;5Hcursor \u001b]0;window title\u0007kept \u001b]8;;http://x.invalid\u001b\\link\u001b]8;;\u001b\\\n";
        Assert.Equal("red cursor kept link\n", Run(Utf8.GetBytes(input), step));
    }

    [Fact]
    public void An_unfinished_escape_sequence_never_swallows_the_next_line()
    {
        Assert.Equal("a\nnext\n", Run(Utf8.GetBytes("a\u001b[31\nnext\n")));
        Assert.Equal("ok\nnext\n", Run(Utf8.GetBytes("ok\u001b]0;title\nnext\n")));
    }

    [Fact]
    public void Invalid_utf8_and_nul_become_the_replacement_character_and_other_controls_vanish()
    {
        Assert.Equal("�a�b", Run([0xFF, (byte)'a', 0x00, (byte)'b']));
        Assert.Equal("x\ty\nz", Run(Utf8.GetBytes("\u0001x\u0007\ty\u007f\u0085\nz")));
        Assert.Equal("a\nb\n", Run(Utf8.GetBytes("a\r\nb\r\n")));
        Assert.Equal("�", Run([0xC3])); // a lone lead byte at the very end
    }

    [Fact]
    public void Multi_byte_characters_cut_by_a_read_boundary_come_out_whole()
    {
        var input = "é€😀\n";
        Assert.Equal(input, Run(Utf8.GetBytes(input), 1));
        Assert.Equal(input, Run(Utf8.GetBytes(input), 2));
    }

    [Fact]
    public void A_secret_split_across_two_reads_is_masked_whole()
    {
        var token = "ghp_" + new string('a', 36);
        var collector = Big();
        var bytes = Utf8.GetBytes("export T=" + token + "\n");
        var cut = bytes.Length / 2;
        collector.Add(Stdout, bytes.AsSpan(0, cut));
        collector.Add(Stdout, bytes.AsSpan(cut));
        collector.Complete();
        var text = Text(Drain(collector));
        Assert.Equal("export T=[masked:github_token]\n", text);
    }

    [Fact]
    public void A_partial_line_is_held_back_until_its_newline_or_the_end()
    {
        var collector = Big();
        collector.Add(Stdout, Utf8.GetBytes("password=hunter22"));
        collector.FlushHead();
        Assert.Empty(Drain(collector)); // nothing of it may leave before the line is complete
        collector.Add(Stdout, Utf8.GetBytes("xyz\n"));
        collector.Complete();
        Assert.Equal("password=[masked:connection_password]\n", Text(Drain(collector)));
    }

    [Fact]
    public void A_private_key_block_across_lines_is_masked_as_one_and_the_text_around_it_is_kept()
    {
        var input = "before\n" + Begin("RSA ") + "\nMIIEowIBAAKCAQEA\nabcdef\n" + End("RSA ") + "\nafter\n";
        var collector = Big();
        foreach (var line in input.Split('\n', StringSplitOptions.RemoveEmptyEntries)) collector.Add(Stdout, Utf8.GetBytes(line + "\n"));
        collector.Complete();
        var text = Text(Drain(collector));
        Assert.Equal("before\n[masked:private_key]\nafter\n", text);
    }

    [Fact]
    public void A_private_key_that_never_ends_is_replaced_by_a_marker_at_the_end_of_the_stream()
    {
        var text = Run(Utf8.GetBytes("a\n" + Begin("") + "\nMIIsecretsecret\n"));
        Assert.Equal("a\n" + OutputSanitizer.KeyMarker + "\n", text);
    }

    [Fact]
    public void A_private_key_longer_than_the_cap_becomes_one_marker_and_the_output_after_it_survives()
    {
        var builder = new StringBuilder(Begin("") + "\n");
        for (var i = 0; i < 1100; i++) builder.Append(new string('A', 63)).Append('\n');
        builder.Append(End("") + "\nafter\n");
        Assert.Equal(OutputSanitizer.KeyMarker + "\nafter\n", Run(Utf8.GetBytes(builder.ToString())));
    }

    [Fact]
    public void Output_beyond_the_head_keeps_a_tail_with_exactly_one_gap_marker_and_a_gapless_numbering()
    {
        var collector = new OutputCollector(100, 100, 64, 1L << 30);
        var lines = Enumerable.Range(0, 400).Select(i => $"line {i:D4}\n").ToList();
        foreach (var line in lines) collector.Add(Stdout, Utf8.GetBytes(line));
        collector.Complete();

        var chunks = Drain(collector);
        var all = string.Concat(lines);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Seq));
        var gap = Assert.Single(chunks, c => c.GapBefore);
        var firstTail = chunks.First(c => c.Seq > 0 && chunks.Take(c.Seq).Sum(x => Utf8.GetByteCount(x.Body)) >= 100);
        Assert.Same(gap, firstTail);

        var head = Text(chunks.TakeWhile(c => !c.GapBefore));
        var tail = Text(chunks.SkipWhile(c => !c.GapBefore));
        Assert.Equal(100, Utf8.GetByteCount(head));
        Assert.StartsWith(head, all, StringComparison.Ordinal);
        Assert.Equal(100, Utf8.GetByteCount(tail));
        Assert.EndsWith(tail, all, StringComparison.Ordinal);
        Assert.True(collector.Truncated);
        Assert.Equal(200, collector.BytesKept);
    }

    [Fact]
    public void Output_that_fits_the_head_has_no_gap_and_is_not_truncated()
    {
        var collector = new OutputCollector(1000, 1000, 64, 1L << 30);
        collector.Add(Stdout, Utf8.GetBytes(new string('a', 150) + "\n"));
        collector.Complete();
        var chunks = Drain(collector);
        Assert.DoesNotContain(chunks, c => c.GapBefore);
        Assert.False(collector.Truncated);
        Assert.Equal(151, collector.BytesKept);
        Assert.Equal(new string('a', 150) + "\n", Text(chunks));
    }

    [Fact]
    public void Output_exactly_filling_the_head_and_tail_together_is_not_cut()
    {
        var collector = new OutputCollector(50, 50, 64, 1L << 30);
        collector.Add(Stdout, Utf8.GetBytes(new string('a', 99) + "\n"));
        collector.Complete();
        Assert.False(collector.Truncated);
        Assert.Equal(100, Utf8.GetByteCount(Text(Drain(collector))));
    }

    [Fact]
    public void No_chunk_is_larger_than_the_limit_in_bytes_and_no_character_is_split()
    {
        var collector = new OutputCollector(1 << 20, 1 << 20, 20, 1L << 30);
        var input = string.Concat(Enumerable.Repeat("é😀€x", 120)) + "\n";
        collector.Add(Stdout, Utf8.GetBytes(input));
        collector.Complete();
        var chunks = Drain(collector);
        Assert.True(chunks.Count > 10);
        Assert.All(chunks, c => Assert.True(Utf8.GetByteCount(c.Body) <= 20, $"chunk {c.Seq} has {Utf8.GetByteCount(c.Body)} bytes"));
        Assert.All(chunks, c => Assert.DoesNotContain('�', c.Body));
        Assert.Equal(input, Text(chunks));
    }

    [Fact]
    public void A_chunk_never_mixes_the_two_streams()
    {
        var collector = Big();
        collector.Add(Stdout, Utf8.GetBytes("out1\n"));
        collector.Add(Stderr, Utf8.GetBytes("err1\n"));
        collector.Add(Stdout, Utf8.GetBytes("out2\n"));
        collector.Complete();
        Assert.Equal([(Stdout, "out1\n"), (Stderr, "err1\n"), (Stdout, "out2\n")], Drain(collector).Select(c => (c.Stream, c.Body)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Big().Add("stdin", [1]));
    }

    [Fact]
    public void Flushing_the_head_sends_the_partly_filled_chunk_and_numbering_goes_on()
    {
        var collector = Big();
        collector.Add(Stdout, Utf8.GetBytes("first\n"));
        Assert.Empty(Drain(collector)); // not full, not flushed
        collector.FlushHead();
        var early = Assert.Single(Drain(collector));
        Assert.Equal((0, "first\n"), (early.Seq, early.Body));
        collector.Add(Stdout, Utf8.GetBytes("second\n"));
        collector.Complete();
        var late = Assert.Single(Drain(collector));
        Assert.Equal((1, "second\n"), (late.Seq, late.Body));
    }

    [Fact]
    public async Task Pumping_a_stream_reads_it_to_the_end()
    {
        var collector = Big();
        await collector.PumpAsync(Stderr, new MemoryStream(Utf8.GetBytes("from a pipe\n")), CancellationToken.None);
        collector.Complete();
        var chunk = Assert.Single(Drain(collector));
        Assert.Equal((Stderr, "from a pipe\n"), (chunk.Stream, chunk.Body));
    }

    [Fact]
    public async Task Reading_more_than_the_cap_sets_the_flag_completes_the_wait_and_discards_the_rest()
    {
        var collector = new OutputCollector(1000, 1000, 64, 100);
        collector.Add(Stdout, Utf8.GetBytes(new string('a', 59) + "\n"));
        Assert.False(collector.OverReadCap);
        Assert.False(collector.CapReached.IsCompleted);

        collector.Add(Stdout, Utf8.GetBytes(new string('b', 59) + "\n"));
        Assert.True(collector.OverReadCap);
        await collector.CapReached.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(collector.Truncated);
        Assert.Equal(120, collector.BytesRead);

        collector.Add(Stdout, Utf8.GetBytes("c\n"));
        collector.Complete();
        Assert.Equal(new string('a', 59) + "\n", Text(Drain(collector)));
    }

    [Fact]
    public void Reading_exactly_the_cap_is_still_allowed()
    {
        var collector = new OutputCollector(1000, 1000, 64, 10);
        collector.Add(Stdout, Utf8.GetBytes("123456789\n"));
        Assert.False(collector.OverReadCap);
        Assert.False(collector.Truncated);
    }

    [Fact]
    public void The_chunks_channel_completes_after_Complete_and_a_second_Complete_changes_nothing()
    {
        var collector = Big();
        collector.Add(Stdout, Utf8.GetBytes("x\n"));
        collector.Complete();
        collector.Complete();
        Assert.Single(Drain(collector));
        Assert.True(collector.Chunks.Completion.IsCompleted); // completes once the last chunk is read
    }

    // Fake key markers, built here so no secret scanner reads a key in the source (the tests need only the markers).
    private static string Begin(string kind) => "-----BEGIN " + kind + "PRIVATE " + "KEY-----";

    private static string End(string kind) => "-----END " + kind + "PRIVATE " + "KEY-----";
}
