using System.Text;

namespace IlRepl.Processes;

/// <summary>
/// Drains inherited diagnostic streams while retaining at most 64 KiB of complete UTF-8 text.
/// </summary>
internal sealed class DiagnosticTail
{
    private readonly StringBuilder _text = new();
    private string? _marker;
    private TaskCompletionSource? _boundary;

    /// <summary>
    /// Arms acknowledgement of a unique diagnostic boundary before its writer publishes it.
    /// </summary>
    /// <param name="marker">The unique complete text to remove from retained diagnostics.</param>
    /// <returns>Completion after the preceding bytes have reached this reader.</returns>
    internal Task Mark(string marker)
    {
        lock (_text)
        {
            _marker = marker;
            _boundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _boundary.Task;
        }
    }

    /// <summary>
    /// Appends decoded diagnostic text without splitting a retained surrogate pair.
    /// </summary>
    /// <param name="text">The next bounded read.</param>
    internal void Append(ReadOnlySpan<char> text)
    {
        lock (_text)
        {
            _text.Append(text);
            var content = _text.ToString();
            if (_marker is { } marker && content.IndexOf(marker, StringComparison.Ordinal) is >= 0 and var index)
            {
                _text.Remove(index, marker.Length);
                _marker = null;
                _boundary!.TrySetResult();
                content = _text.ToString();
            }
            if (Encoding.UTF8.GetByteCount(content) <= 65536) return;
            var remove = Math.Max(1, content.Length - 65536);
            while (remove < content.Length && Encoding.UTF8.GetByteCount(content.AsSpan(remove)) > 65536)
                remove += Math.Max(1, (content.Length - remove) / 4);
            if (remove < content.Length && char.IsLowSurrogate(content[remove])) remove++;
            _text.Remove(0, remove);
        }
    }

    /// <summary>
    /// Continuously drains one stream without accumulating an unbounded line before its newline arrives.
    /// </summary>
    /// <param name="reader">The directly inherited diagnostic pipe reader.</param>
    /// <param name="destination">The current runtime's diagnostic buffer.</param>
    /// <returns>Completion when the last inherited writer closes the pipe.</returns>
    internal static async Task DrainAsync(StreamReader reader, Func<DiagnosticTail> destination)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                if (read == 0) return;
                destination().Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    /// <summary>
    /// Captures the bounded diagnostic text retained so far.
    /// </summary>
    /// <returns>The complete retained text.</returns>
    public override string ToString()
    {
        lock (_text) return _text.ToString();
    }
}
