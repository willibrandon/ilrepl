namespace IlRepl.Tui;

/// <summary>
/// Keeps terminal control strings complete across native reads without changing ordinary input or bracketed paste.
/// </summary>
internal sealed class TerminalReplyBuffer
{
    private const int MaximumReplyLength = 65_536;
    private static readonly byte[] EscapeMarker = "\u001b]7777;ilrepl-escape\a"u8.ToArray();
    private readonly List<byte> _reply = [];
    private readonly Queue<ReadOnlyMemory<byte>> _ready = [];
    private bool _escape;
    private byte _stringKind;
    private bool _stringEscape;
    private bool _overflow;
    private int _pasteStart;
    private int _pasteEnd;
    private bool _paste;

    /// <summary>
    /// Gets whether a lone Escape still needs the normal keyboard ambiguity deadline.
    /// </summary>
    internal bool NeedsEscapeTimeout => _escape;

    /// <summary>
    /// Returns ordinary input immediately and releases a bounded control string only when its terminator arrives.
    /// </summary>
    /// <param name="input">The next complete native read, including any split UTF-8 bytes.</param>
    /// <returns>Input that is ready for the terminal parser, or no bytes while a control string remains incomplete.</returns>
    internal ReadOnlyMemory<byte> Append(ReadOnlyMemory<byte> input)
    {
        if (_ready.Count == 0 && !_escape && _stringKind == 0 && !_paste && _pasteStart == 0
            && !input.Span.Contains((byte)27)) return input;
        var output = new List<byte>(input.Length);
        foreach (var value in input.Span)
        {
            if (_paste)
            {
                output.Add(value);
                var end = "\u001b[201~"u8;
                _pasteEnd = value == end[_pasteEnd] ? _pasteEnd + 1 : value == 27 ? 1 : 0;
                if (_pasteEnd == end.Length) { _paste = false; _pasteEnd = 0; }
                continue;
            }

            if (_stringKind != 0)
            {
                if (!_overflow)
                {
                    if (_reply.Count < MaximumReplyLength) _reply.Add(value);
                    else { _reply.Clear(); _overflow = true; }
                }
                if (_stringEscape && value == '\\' || _stringKind == ']' && value == 7)
                {
                    if (!_overflow) output.AddRange(_reply);
                    _reply.Clear();
                    _stringKind = 0;
                    _stringEscape = false;
                    _overflow = false;
                }
                else _stringEscape = value == 27;
                continue;
            }

            if (_escape)
            {
                _escape = false;
                if (value == 27)
                {
                    // The next Escape disambiguates the prior key. Keep its marker in a standalone parser batch.
                    if (output.Count != 0) { _ready.Enqueue(output.ToArray()); output.Clear(); }
                    _ready.Enqueue(EscapeMarker);
                    _escape = true;
                    _pasteStart = 0;
                    continue;
                }
                if (value is (byte)']' or (byte)'_')
                {
                    _stringKind = value;
                    _reply.Add(27);
                    _reply.Add(value);
                    _pasteStart = 0;
                    continue;
                }
                output.Add(27);
                _pasteStart = value == '[' ? 1 : 0;
            }
            else if (_pasteStart != 0)
            {
                var start = "[200~"u8;
                _pasteStart = value == start[_pasteStart] ? _pasteStart + 1 : 0;
                if (_pasteStart == start.Length) { _paste = true; _pasteStart = 0; }
            }

            if (value == 27) _escape = true;
            else output.Add(value);
        }
        if (output.Count != 0) _ready.Enqueue(output.ToArray());
        return ReadBuffered();
    }

    /// <summary>
    /// Returns the next already-framed segment before the caller admits another native read.
    /// </summary>
    /// <returns>A standalone Escape marker or ordinary input, or no bytes when the queue has drained.</returns>
    internal ReadOnlyMemory<byte> ReadBuffered() => _ready.TryDequeue(out var bytes) ? bytes : ReadOnlyMemory<byte>.Empty;

    /// <summary>
    /// Emits an unambiguous Escape key after its deadline without exposing any unfinished terminal reply.
    /// </summary>
    /// <returns>A complete Escape key sequence, or no input when a reply is pending.</returns>
    internal ReadOnlyMemory<byte> FlushEscape()
    {
        if (!_escape) return ReadOnlyMemory<byte>.Empty;
        _escape = false;
        _pasteStart = 0;
        _ready.Enqueue(EscapeMarker);
        return ReadBuffered();
    }

    /// <summary>
    /// Drops unfinished framing state after all readers have settled at the end of raw mode.
    /// </summary>
    internal void Reset()
    {
        _reply.Clear();
        _ready.Clear();
        _escape = false;
        _stringKind = 0;
        _stringEscape = false;
        _overflow = false;
        _pasteStart = 0;
        _pasteEnd = 0;
        _paste = false;
    }
}
