using System.Text;
using System.Text.RegularExpressions;
using ClaudeMonitor.Agent.Capture;

namespace ClaudeMonitor.Agent.Exec;

/// <summary>
/// One output stream of a run, from raw bytes to masked, printable text (ADR-0004, "Output"): UTF-8 decoded with
/// replacement, NUL and invalid bytes shown as U+FFFD, C0 controls (except line feed and tab), C1 controls and ANSI
/// escape sequences removed, then masked line by line. A line is held until its newline, so a secret is never cut in two
/// by a read; a private-key block is held from BEGIN to END and masked whole. Not thread-safe: one reader owns it.
/// </summary>
internal sealed partial class OutputSanitizer
{
    public const int MaxLineChars = 64 * 1024;
    public const int MaxKeyChars = 64 * 1024;
    public const string KeyMarker = "[masked: private key]";
    private const int MaxSequenceChars = 4096;
    private const char Replacement = '�';
    private const char Escape = '\u001b';

    private enum State { Text, Escape, EscapeIntermediate, Csi, String, StringEscape }

    private enum KeyState { None, Collecting, Discarding }

    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _key = new();
    private char[] _chars = new char[4096];
    private State _state = State.Text;
    private KeyState _keyState = KeyState.None;
    private int _sequenceLength;

    /// <summary>Appends whatever lines are complete after these bytes to <paramref name="output"/>.</summary>
    public void Feed(ReadOnlySpan<byte> bytes, StringBuilder output) => Decode(bytes, false, output);

    /// <summary>The stream ended: the held partial line (and a key block never closed) is emitted.</summary>
    public void Finish(StringBuilder output)
    {
        Decode([], true, output);
        _state = State.Text;
        if (_line.Length > 0) EndLine(output, false);
        if (_keyState != KeyState.None)
        {
            output.Append(KeyMarker).Append('\n');
            _key.Clear();
            _keyState = KeyState.None;
        }
    }

    private void Decode(ReadOnlySpan<byte> bytes, bool flush, StringBuilder output)
    {
        var max = Encoding.UTF8.GetMaxCharCount(bytes.Length) + 4;
        if (_chars.Length < max) _chars = new char[max];
        var count = _decoder.GetChars(bytes, _chars, flush);
        for (var i = 0; i < count; i++) Step(_chars[i], output);
    }

    private void Step(char c, StringBuilder output)
    {
        if (_state != State.Text && c == '\n') _state = State.Text; // an unfinished sequence never swallows a line end
        if (_state != State.Text && ++_sequenceLength > MaxSequenceChars) _state = State.Text;
        switch (_state)
        {
            case State.Text:
                InText(c, output);
                break;
            case State.Escape:
                if (c == '[') _state = State.Csi;
                else if (c is ']' or 'P' or 'X' or '^' or '_') _state = State.String;
                else if (c == Escape) _sequenceLength = 0;
                else if (c is >= ' ' and <= '/') _state = State.EscapeIntermediate;
                else EndSequence(c, c is >= '0' and <= '~', output);
                break;
            case State.EscapeIntermediate:
                if (c is < ' ' or > '/') EndSequence(c, c is >= '0' and <= '~', output);
                break;
            case State.Csi:
                if (c == Escape) Begin(State.Escape);
                else if (c is < ' ' or > '?') EndSequence(c, c is >= '@' and <= '~', output);
                break;
            case State.String:
                if (c == Escape) _state = State.StringEscape;
                else if (c is '\a' or '\u009c') _state = State.Text;
                break;
            case State.StringEscape:
                if (c == '\\') _state = State.Text;
                else
                {
                    Begin(State.Escape);
                    Step(c, output);
                }

                break;
            default:
                _state = State.Text;
                break;
        }
    }

    // The sequence is over: with its final character (swallowed), or because something else came (handled as text).
    private void EndSequence(char c, bool final, StringBuilder output)
    {
        _state = State.Text;
        if (!final) InText(c, output);
    }

    private void Begin(State state)
    {
        _state = state;
        _sequenceLength = 0;
    }

    private void InText(char c, StringBuilder output)
    {
        switch (c)
        {
            case '\n':
                EndLine(output, true);
                return;
            case '\t':
                break;
            case Escape:
                Begin(State.Escape);
                return;
            case '\0':
                c = Replacement;
                break;
            case '\u009b':
                Begin(State.Csi);
                return;
            case '\u0090' or '\u0098' or '\u009d' or '\u009e' or '\u009f':
                Begin(State.String);
                return;
            default:
                if (c < ' ' || (c >= '\u007f' && c <= '\u009f')) return; // C0 (CR included), DEL and the other C1 controls
                break;
        }

        _line.Append(c);
        if (_line.Length >= MaxLineChars && !char.IsHighSurrogate(c)) EndLine(output, false);
    }

    private void EndLine(StringBuilder output, bool newline)
    {
        var text = _line.ToString();
        _line.Clear();
        Line(text, newline, output);
    }

    private void Line(string text, bool newline, StringBuilder output)
    {
        switch (_keyState)
        {
            case KeyState.None:
                var begin = KeyBegin().Matches(text) is { Count: > 0 } all ? all[^1] : null;
                if (begin is not null && !KeyEnd().IsMatch(text, begin.Index + begin.Length))
                {
                    output.Append(Masker.MaskText(text[..begin.Index]));
                    _key.Clear().Append(text, begin.Index, text.Length - begin.Index);
                    if (newline) _key.Append('\n');
                    _keyState = KeyState.Collecting;
                    return;
                }

                output.Append(Masker.MaskText(text));
                if (newline) output.Append('\n');
                return;
            case KeyState.Collecting:
                var end = KeyEnd().Match(text);
                if (end.Success)
                {
                    var upTo = end.Index + end.Length;
                    output.Append(Masker.MaskText(_key.Append(text, 0, upTo).ToString()));
                    _key.Clear();
                    _keyState = KeyState.None;
                    Line(text[upTo..], newline, output); // what follows the END marker is ordinary text
                    return;
                }

                _key.Append(text);
                if (newline) _key.Append('\n');
                if (_key.Length > MaxKeyChars)
                {
                    output.Append(KeyMarker).Append('\n');
                    _key.Clear();
                    _keyState = KeyState.Discarding;
                }

                return;
            case KeyState.Discarding:
                var last = KeyEnd().Match(text);
                if (last.Success)
                {
                    _keyState = KeyState.None;
                    var rest = text[(last.Index + last.Length)..];
                    if (rest.Length > 0) Line(rest, newline, output); // the marker already ended its line
                }

                return;
            default:
                throw new InvalidOperationException("Unknown key state.");
        }
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex KeyBegin();

    [GeneratedRegex(@"-----END [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex KeyEnd();
}
