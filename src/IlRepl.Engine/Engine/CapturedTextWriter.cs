using System.Text;

namespace IlRepl.Engine;

/// <summary>
/// Retains captured output and applies synchronous backpressure to bounded streaming notifications.
/// </summary>
internal sealed class CapturedTextWriter(bool error, Action<string, bool>? output) : TextWriter
{
    private readonly StringBuilder _text = new();
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public override Encoding Encoding => Encoding.UTF8;

    /// <inheritdoc />
    public override void Write(char value) => Write(value.ToString());

    /// <inheritdoc />
    public override void Write(char[] buffer, int index, int count) => Write(buffer.AsSpan(index, count));

    /// <inheritdoc />
    public override void Write(string? value)
    {
        if (value is not null)
        {
            Write(value.AsSpan());
        }
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<char> buffer)
    {
        lock (_lock)
        {
            while (!buffer.IsEmpty)
            {
                var length = Math.Min(buffer.Length, 8192);
                var chunk = buffer[..length].ToString();
                _text.Append(chunk);
                if (output is not null && _text.Length > 65536)
                {
                    _text.Remove(0, _text.Length - 65536);
                }

                output?.Invoke(chunk, error);
                buffer = buffer[length..];
            }
        }
    }

    /// <inheritdoc />
    public override void WriteLine(string? value)
    {
        lock (_lock)
        {
            Write(value);
            Write(NewLine);
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        lock (_lock)
        {
            return _text.ToString();
        }
    }
}
