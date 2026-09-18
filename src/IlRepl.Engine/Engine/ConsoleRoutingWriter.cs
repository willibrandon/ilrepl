using System.Text;

namespace IlRepl.Engine;

/// <summary>
/// Routes console writes to the current logical capture while preserving the original writer outside execution.
/// </summary>
internal sealed class ConsoleRoutingWriter(TextWriter fallback, bool error) : TextWriter
{
    private TextWriter Target => ConsoleCapture.Writer(error) ?? fallback;

    /// <inheritdoc />
    public override Encoding Encoding => Target.Encoding;

    /// <inheritdoc />
    public override IFormatProvider FormatProvider => Target.FormatProvider;

    /// <inheritdoc />
    public override void Write(char value) => Target.Write(value);

    /// <inheritdoc />
    public override void Write(char[] buffer, int index, int count) => Target.Write(buffer, index, count);

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<char> buffer) => Target.Write(buffer);

    /// <inheritdoc />
    public override void Write(string? value) => Target.Write(value);

    /// <inheritdoc />
    public override void WriteLine(string? value) => Target.WriteLine(value);

    /// <inheritdoc />
    public override void Flush() => Target.Flush();
}
