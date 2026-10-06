using System.Text;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>
/// The ring of the last <c>maxBytes</c> of a run's output, in UTF-8 bytes: the oldest text goes first, whole segments and
/// then the front of the oldest one. Each segment holds text of one stream. Not thread-safe; the collector locks.
/// </summary>
internal sealed class OutputTail(int maxBytes)
{
    private const int SegmentBytes = 8192;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    internal sealed class Segment(string stream)
    {
        public string Stream { get; } = stream;
        public StringBuilder Text { get; } = new();
        public int Bytes { get; set; }
    }

    private readonly LinkedList<Segment> _segments = new();

    public long Bytes { get; private set; }

    /// <summary>Bytes pushed out of the ring so far.</summary>
    public long Dropped { get; private set; }

    public IEnumerable<Segment> Segments => _segments;

    public void Add(string stream, string text)
    {
        for (var pos = 0; pos < text.Length;)
        {
            var take = Math.Min(SegmentBytes, text.Length - pos);
            if (take < text.Length - pos && char.IsHighSurrogate(text[pos + take - 1])) take--;
            var piece = text.Substring(pos, take);
            pos += take;

            var segment = _segments.Last?.Value;
            if (segment is null || segment.Stream != stream || segment.Bytes >= SegmentBytes)
            {
                segment = new Segment(stream);
                _segments.AddLast(segment);
            }

            var bytes = Utf8.GetByteCount(piece);
            segment.Text.Append(piece);
            segment.Bytes += bytes;
            Bytes += bytes;
            Trim();
        }
    }

    private void Trim()
    {
        while (Bytes > maxBytes && _segments.First is { } first)
        {
            var excess = Bytes - maxBytes;
            var segment = first.Value;
            var removed = segment.Bytes;
            if (segment.Bytes <= excess)
            {
                _segments.RemoveFirst();
            }
            else
            {
                var text = segment.Text.ToString();
                var cut = FitChars(text, (int)excess);
                removed = Utf8.GetByteCount(text.AsSpan(0, cut));
                if (removed < excess)
                {
                    cut += char.IsHighSurrogate(text[cut]) ? 2 : 1; // one more whole character: at least the excess goes
                    removed = Utf8.GetByteCount(text.AsSpan(0, cut));
                }

                segment.Text.Remove(0, cut);
                segment.Bytes -= removed;
            }

            Bytes -= removed;
            Dropped += removed;
        }
    }

    /// <summary>How many leading chars of text fit in maxBytes of UTF-8, never splitting a surrogate pair.</summary>
    public static int FitChars(string text, int maxBytes)
    {
        if (maxBytes <= 0) return 0;
        if (Utf8.GetByteCount(text) <= maxBytes) return text.Length;
        var bytes = 0;
        var i = 0;
        while (i < text.Length)
        {
            var pair = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            var width = pair ? 4 : text[i] < 0x80 ? 1 : text[i] < 0x800 ? 2 : 3;
            if (bytes + width > maxBytes) break;
            bytes += width;
            i += pair ? 2 : 1;
        }

        return i;
    }
}
