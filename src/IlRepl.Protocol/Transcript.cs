namespace IlRepl.Protocol;

/// <summary>
/// The scrollback of a session. Old lines are dropped once <see cref="MaxLines"/> is exceeded.
/// </summary>
public sealed class Transcript
{
    private readonly List<TranscriptLine> _lines = [];

    /// <summary>
    /// The lines, oldest first.
    /// </summary>
    public IReadOnlyList<TranscriptLine> Lines => _lines;

    /// <summary>
    /// The most lines kept. Zero or less keeps everything.
    /// </summary>
    public int MaxLines { get; set; } = 2000;

    /// <summary>
    /// Increments on every change, so a view can tell whether it needs to re-render.
    /// </summary>
    public long Version { get; private set; }

    /// <summary>
    /// The number of lines ever added, including dropped ones.
    /// </summary>
    public long TotalAdded { get; private set; }

    /// <summary>
    /// Appends a line.
    /// </summary>
    /// <param name="line">The line.</param>
    public void Add(TranscriptLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        _lines.Add(line);
        TotalAdded++;
        Version++;
        if (MaxLines > 0 && _lines.Count > MaxLines)
        {
            _lines.RemoveRange(0, _lines.Count - MaxLines);
        }
    }

    /// <summary>
    /// Appends a single-span line.
    /// </summary>
    /// <param name="kind">What the line represents.</param>
    /// <param name="text">The text.</param>
    /// <param name="style">The style.</param>
    public void Add(LineKind kind, string text, SpanStyle style = SpanStyle.Default) => Add(TranscriptLine.Of(kind, text, style));

    /// <summary>
    /// Removes every line.
    /// </summary>
    public void Clear()
    {
        _lines.Clear();
        Version++;
    }
}
