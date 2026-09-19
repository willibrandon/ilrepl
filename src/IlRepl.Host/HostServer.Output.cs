using IlRepl.Protocol;

namespace IlRepl.Host;

/// <summary>
/// Publishes acknowledged output chunks while preserving complete reply output for nonstreaming clients.
/// </summary>
public sealed partial class HostServer
{
    private readonly Lock _outputLock = new();
    private string? _outputIdentity;
    private long _outputSequence;
    private readonly List<TranscriptLine> _pendingOutputLines = [];
    private readonly HashSet<TranscriptLine> _streamedOutputLines = new(ReferenceEqualityComparer.Instance);

    private void RetainOutputPrefix(TranscriptLine line)
    {
        if (line.Kind == LineKind.Output)
        {
            return;
        }

        lock (_outputLock)
        {
            BeginOutputOperation();
            _pendingOutputLines.Add(line);
        }
    }

    private void BeginOutputOperation()
    {
        var identity = _engine.Progress.Identity;
        if (identity == _outputIdentity)
        {
            return;
        }

        _outputIdentity = identity;
        _outputSequence = 0;
        _pendingOutputLines.Clear();
        _streamedOutputLines.Clear();
    }

    private void PublishOutput(string text, bool error)
    {
        if (_client is not { } client)
        {
            return;
        }

        lock (_outputLock)
        {
            BeginOutputOperation();
            var leading = _pendingOutputLines.ToArray();
            client.OutputAsync(new ExecutionOutput(_outputIdentity!, ++_outputSequence, text, error) { LeadingLines = leading },
                CancellationToken.None)
                .GetAwaiter().GetResult();
            _streamedOutputLines.UnionWith(leading);
            _pendingOutputLines.Clear();
        }
    }

    private HandleReply WithStreamedOutput(HandleReply reply)
    {
        lock (_outputLock)
        {
            return _engine.Progress.Identity == _outputIdentity
                ? reply with
                {
                    OutputIdentity = _outputIdentity,
                    OutputSequence = _outputSequence,
                    StreamedLineIndexes = [.. reply.Lines.Select((line, index) => (line, index))
                        .Where(item => _streamedOutputLines.Contains(item.line)).Select(item => item.index)],
                }
                : reply;
        }
    }
}
