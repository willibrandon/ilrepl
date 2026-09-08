namespace IlRepl.Tests.Tui;

/// <summary>
/// One rendered frame: the screen's rows and where the prompt painted its caret cell.
/// </summary>
/// <param name="Index">The frame's ordinal since the session started.</param>
/// <param name="Lines">The rows, top to bottom.</param>
/// <param name="Caret">The caret cell, or null when no cell carries the prompt colour as its background.</param>
/// <param name="Width">The width in columns.</param>
/// <param name="Height">The height in rows.</param>
internal sealed record Frame(int Index, IReadOnlyList<string> Lines, (int X, int Y)? Caret, int Width, int Height)
{
    /// <summary>
    /// The row the caret cell is on, or null.
    /// </summary>
    public string? CaretRow => Caret is { } c ? Lines[c.Y] : null;

    /// <summary>
    /// True when a row contains the text.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>True when some row contains it.</returns>
    public bool Contains(string text) => Lines.Any(l => l.Contains(text, StringComparison.Ordinal));

    /// <inheritdoc />
    public override string ToString() => $"frame {Index} caret {Caret}\n" + string.Join('\n', Lines.Select((l, i) => $"{i,2}|{l}"));
}
