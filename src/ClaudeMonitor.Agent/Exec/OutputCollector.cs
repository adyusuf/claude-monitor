using System.Text;
using System.Threading.Channels;
using ClaudeMonitor.Agent.Config;
using ClaudeMonitor.Contracts;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>
/// Collects what a run prints (ADR-0004, "Output"). Two reader tasks feed it raw bytes through <see cref="Add"/> (or
/// <see cref="PumpAsync"/>), each stream through its own <see cref="OutputSanitizer"/>. What survives is kept as the first
/// <c>RunHeadBytes</c>, then only a ring of the last <c>RunTailBytes</c>. Output leaves as <see cref="RunOutputChunk"/>s of
/// at most <c>RunChunkBytes</c> UTF-8 bytes, numbered from 0, through <see cref="Chunks"/>: head chunks as they fill (or on
/// <see cref="FlushHead"/>), tail chunks only at <see cref="Complete"/>, the first of them marked GapBefore when bytes were
/// dropped between. The channel is unbounded but holds at most head + tail bytes. Sizes count masked UTF-8 bytes; the read
/// cap counts raw bytes.
/// </summary>
public sealed class OutputCollector
{
    private const int MinChunkBytes = 16;
    private const int ReadBufferBytes = 16 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private sealed class Lane(string stream)
    {
        public string Stream { get; } = stream;
        public OutputSanitizer Sanitizer { get; } = new();
        public StringBuilder Text { get; } = new();
    }

    private readonly int _headMax;
    private readonly OutputTail _tail;
    private readonly int _chunkMax;
    private readonly long _readCap;
    private readonly Lane _stdout = new(RunStreams.Stdout);
    private readonly Lane _stderr = new(RunStreams.Stderr);
    private readonly Channel<RunOutputChunk> _channel = Channel.CreateUnbounded<RunOutputChunk>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _capReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _sink = new();
    private readonly StringBuilder _pending = new();
    private string? _pendingStream;
    private int _pendingBytes;
    private int _seq;
    private long _headUsed;
    private bool _discarded;
    private bool _headFull;
    private bool _gapNext;
    private bool _completed;
    private long _read;

    public OutputCollector(AgentConfig config) : this(config.RunHeadBytes, config.RunTailBytes, config.RunChunkBytes, config.RunReadCap)
    {
    }

    public OutputCollector(int headBytes, int tailBytes, int chunkBytes, long readCap)
    {
        _headMax = Math.Max(0, headBytes);
        _tail = new OutputTail(Math.Max(0, tailBytes));
        _chunkMax = Math.Max(MinChunkBytes, chunkBytes);
        _readCap = readCap;
    }

    /// <summary>Where the chunks come out; completes after <see cref="Complete"/>.</summary>
    public ChannelReader<RunOutputChunk> Chunks => _channel.Reader;

    /// <summary>Completes when more than the read cap came in; the caller then kills the run. Later bytes are discarded.</summary>
    public Task CapReached => _capReached.Task;

    public bool OverReadCap => _capReached.Task.IsCompleted;

    public long BytesRead => Interlocked.Read(ref _read);

    /// <summary>Masked bytes kept (head and tail), not counting what was dropped.</summary>
    public long BytesKept
    {
        get
        {
            lock (_sink) return _headUsed + _tail.Bytes;
        }
    }

    /// <summary>True when output was dropped between head and tail, or discarded after the read cap.</summary>
    public bool Truncated
    {
        get
        {
            lock (_sink) return _tail.Dropped > 0 || _discarded;
        }
    }

    /// <summary>Feeds raw bytes of one stream. One writer per stream at a time (the two streams may run in parallel).</summary>
    public void Add(string stream, ReadOnlySpan<byte> bytes)
    {
        var lane = LaneOf(stream);
        if (Interlocked.Add(ref _read, bytes.Length) > _readCap)
        {
            lock (_sink) _discarded = true;
            _capReached.TrySetResult();
            return;
        }

        lock (lane)
        {
            lane.Text.Clear();
            lane.Sanitizer.Feed(bytes, lane.Text);
            Emit(lane);
        }
    }

    /// <summary>Reads one stream to its end into <see cref="Add"/>.</summary>
    public async Task PumpAsync(string stream, Stream source, CancellationToken cancel)
    {
        var buffer = new byte[ReadBufferBytes];
        while (true)
        {
            var n = await source.ReadAsync(buffer, cancel).ConfigureAwait(false);
            if (n == 0) return;
            Add(stream, buffer.AsSpan(0, n));
        }
    }

    /// <summary>Sends the partly filled head chunk now, so a slow run's output shows up before the chunk is full.</summary>
    public void FlushHead()
    {
        lock (_sink)
        {
            if (!_headFull && !_completed) FlushPending();
        }
    }

    /// <summary>Both streams ended: held lines are emitted, the tail goes out as chunks and <see cref="Chunks"/> completes.</summary>
    public void Complete()
    {
        foreach (var lane in new[] { _stdout, _stderr })
        {
            lock (lane)
            {
                lane.Text.Clear();
                lane.Sanitizer.Finish(lane.Text);
                Emit(lane);
            }
        }

        lock (_sink)
        {
            if (_completed) return;
            _completed = true;
            FlushPending();
            _gapNext = _tail.Dropped > 0;
            foreach (var segment in _tail.Segments) Append(segment.Stream, segment.Text.ToString());
            FlushPending();
            _channel.Writer.TryComplete();
        }
    }

    private Lane LaneOf(string stream) => stream switch
    {
        RunStreams.Stdout => _stdout,
        RunStreams.Stderr => _stderr,
        _ => throw new ArgumentOutOfRangeException(nameof(stream), "Unknown output stream."),
    };

    private void Emit(Lane lane)
    {
        if (lane.Text.Length == 0) return;
        var text = lane.Text.ToString();
        lock (_sink)
        {
            if (_completed) return;
            if (!_headFull)
            {
                var cut = OutputTail.FitChars(text, (int)Math.Min(int.MaxValue, _headMax - _headUsed));
                if (cut > 0)
                {
                    var head = text[..cut];
                    Append(lane.Stream, head);
                    _headUsed += Utf8.GetByteCount(head);
                }

                if (cut < text.Length || _headUsed >= _headMax)
                {
                    _headFull = true;
                    FlushPending();
                }

                text = text[cut..];
            }

            if (text.Length > 0) _tail.Add(lane.Stream, text);
        }
    }

    // Adds text to the chunk being filled, sending each chunk as it fills. Under _sink.
    private void Append(string stream, string text)
    {
        if (_pendingStream is not null && _pendingStream != stream) FlushPending();
        _pendingStream = stream;
        while (text.Length > 0)
        {
            var cut = OutputTail.FitChars(text, _chunkMax - _pendingBytes);
            if (cut > 0)
            {
                _pending.Append(text, 0, cut);
                _pendingBytes += Utf8.GetByteCount(text.AsSpan(0, cut));
                text = text[cut..];
            }

            if (text.Length > 0 || _pendingBytes >= _chunkMax)
            {
                FlushPending();
                _pendingStream = stream;
            }
        }
    }

    private void FlushPending()
    {
        if (_pendingBytes == 0 || _pendingStream is null) return;
        _channel.Writer.TryWrite(new RunOutputChunk(_seq++, _pendingStream, _pending.ToString(), _gapNext));
        _gapNext = false;
        _pending.Clear();
        _pendingBytes = 0;
        _pendingStream = null;
    }
}
