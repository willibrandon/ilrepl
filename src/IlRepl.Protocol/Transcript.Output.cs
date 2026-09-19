namespace IlRepl.Protocol;

/// <summary>
/// Appends streamed console chunks without introducing line breaks between writes from one operation.
/// </summary>
public sealed partial class Transcript
{
    private string? _outputIdentity;
    private long _outputSequence;
    private TranscriptLine? _partialOutput;
    private bool _outputError;

    /// <summary>
    /// Applies an acknowledged output chunk once while retaining only the configured transcript history.
    /// </summary>
    /// <param name="output">The ordered bounded chunk from the current execution.</param>
    public void AppendOutput(ExecutionOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (_outputIdentity == output.Identity && _outputSequence >= output.Sequence)
        {
            return;
        }

        foreach (var line in output.LeadingLines)
        {
            Add(line);
        }

        if (_outputIdentity != output.Identity || _outputError != output.IsError)
        {
            _partialOutput = null;
        }

        _outputIdentity = output.Identity;
        _outputSequence = output.Sequence;
        _outputError = output.IsError;
        var remaining = output.Text.AsSpan();
        while (!remaining.IsEmpty)
        {
            var newline = remaining.IndexOf('\n');
            var text = (newline < 0 ? remaining : remaining[..newline]).TrimEnd('\r').ToString();
            if (_partialOutput is { } previous && _lines.Count != 0 && ReferenceEquals(_lines[^1], previous))
            {
                var combined = previous.PlainText + text;
                if (combined.Length > 65536)
                {
                    combined = combined[^65536..];
                }

                _partialOutput = TranscriptLine.Of(LineKind.Output, combined, output.IsError ? SpanStyle.Error : SpanStyle.Output);
                _lines[^1] = _partialOutput;
                Version++;
            }
            else
            {
                _partialOutput = TranscriptLine.Of(LineKind.Output, text, output.IsError ? SpanStyle.Error : SpanStyle.Output);
                Add(_partialOutput);
            }

            if (newline < 0)
            {
                break;
            }

            _partialOutput = null;
            remaining = remaining[(newline + 1)..];
        }
    }
}
