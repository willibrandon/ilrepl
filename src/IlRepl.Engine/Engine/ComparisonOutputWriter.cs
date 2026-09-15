using System.Text;

namespace IlRepl.Engine;

/// <summary>
/// Captures bounded worker output and immediately notifies the supervising runtime when its limit is reached.
/// </summary>
internal sealed class ComparisonOutputWriter(int limit, Action exceeded) : TextWriter
{
    private readonly Lock _gate = new();
    private readonly StringBuilder _text = new();
    private bool _exceeded;

    /// <inheritdoc/>
    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>
    /// The captured output within the configured limit.
    /// </summary>
    internal string Text
    {
        get
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }
    }

    /// <inheritdoc/>
    public override void Write(char value) => Write(value.ToString());

    /// <inheritdoc/>
    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }

        var notify = false;
        lock (_gate)
        {
            var remaining = Math.Max(0, limit - _text.Length);
            _text.Append(value.AsSpan(0, Math.Min(value.Length, remaining)));
            if (value.Length > remaining && !_exceeded)
            {
                _exceeded = true;
                notify = true;
            }
        }

        if (notify)
        {
            exceeded();
        }
    }

    /// <inheritdoc/>
    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<char> buffer) => Write(buffer.ToString());

    /// <inheritdoc/>
    public override void WriteLine(string? value) => Write(value + NewLine);
}
