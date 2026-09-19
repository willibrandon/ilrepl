namespace IlRepl.Protocol;

/// <summary>
/// Delivers bounded console chunks and suppresses only output already acknowledged by a frontend consumer.
/// </summary>
public sealed partial class SessionController
{
    private readonly Lock _outputLock = new();
    private string? _streamedIdentity;
    private long _streamedSequence;
    private IReadOnlyList<TranscriptLine> _frontendOutputLines = [];
    private bool _frontendOutputAcknowledged;

    private void BeginFrontendOutput(IReadOnlyList<TranscriptLine> lines)
    {
        lock (_outputLock)
        {
            _frontendOutputLines = lines;
            _frontendOutputAcknowledged = false;
        }
    }

    private IReadOnlyList<TranscriptLine> UnstreamedFrontendOutput(IReadOnlyList<TranscriptLine> lines)
    {
        lock (_outputLock)
        {
            return _frontendOutputAcknowledged ? [] : lines;
        }
    }

    private void EndFrontendOutput()
    {
        lock (_outputLock)
        {
            _frontendOutputLines = [];
            _frontendOutputAcknowledged = false;
        }
    }

    private void ForwardOutput(ExecutionOutput output)
    {
        lock (_outputLock)
        {
            if (OutputReceived is not { } receive)
            {
                return;
            }

            if (_streamedIdentity == output.Identity && _streamedSequence >= output.Sequence)
            {
                return;
            }

            receive(_frontendOutputLines.Count == 0 ? output
                : output with { LeadingLines = [.. _frontendOutputLines, .. output.LeadingLines] });
            if (_frontendOutputLines.Count != 0)
            {
                _frontendOutputAcknowledged = true;
                _frontendOutputLines = [];
            }

            _streamedIdentity = output.Identity;
            _streamedSequence = output.Sequence;
        }
    }

    private HandleReply SuppressStreamedOutput(HandleReply reply)
    {
        lock (_outputLock)
        {
            return reply.OutputSequence > 0 && reply.OutputIdentity == _streamedIdentity && reply.OutputSequence <= _streamedSequence
                ? reply with
                {
                    Lines = [.. reply.Lines.Where((line, index) => line.Kind != LineKind.Output
                        && !reply.StreamedLineIndexes.Contains(index))],
                    StreamedLineIndexes = [],
                }
                : reply;
        }
    }
}
