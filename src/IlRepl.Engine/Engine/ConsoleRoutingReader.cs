namespace IlRepl.Engine;

/// <summary>
/// Supplies EOF during ordinary cell execution and preserves console input outside a capture.
/// </summary>
internal sealed class ConsoleRoutingReader(TextReader fallback) : TextReader
{
    private TextReader Target => ConsoleCapture.IsCapturing ? Null : fallback;

    /// <inheritdoc />
    public override int Peek() => Target.Peek();

    /// <inheritdoc />
    public override int Read() => Target.Read();

    /// <inheritdoc />
    public override int Read(char[] buffer, int index, int count) => Target.Read(buffer, index, count);

    /// <inheritdoc />
    public override string? ReadLine() => Target.ReadLine();

    /// <inheritdoc />
    public override string ReadToEnd() => Target.ReadToEnd();
}
