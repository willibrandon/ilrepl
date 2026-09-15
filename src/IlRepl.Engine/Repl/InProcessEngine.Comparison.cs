using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Coordinates isolated comparison execution and rejects stale revisions.
/// </summary>
public sealed partial class InProcessEngine
{
    /// <inheritdoc/>
    public Task<HandleReply> CompareAsync(string identity, CancellationToken cancellationToken)
    {
        Task<HandleReply> comparison;
        lock (_analysisLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            comparison = CompareCoreAsync(identity, cancellationToken);
            _analyses.Add(comparison);
        }

        _ = comparison.ContinueWith(RemoveAnalysis, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return comparison;
    }

    private async Task<HandleReply> CompareCoreAsync(string identity, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ComparisonPackage package;
        MethodEdit capturedEdit;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_preparedComparison is not { } prepared || prepared.Ticket.Identity != identity)
            {
                throw new ReplEngineException("the comparison ticket is no longer available; prepare the comparison again");
            }

            package = prepared.Package;
            capturedEdit = prepared.Edit;
            _preparedComparison = null;
            if (!_core.Session.Edits.Contains(capturedEdit) || capturedEdit.Revision != package.Revision)
            {
                throw new ReplEngineException("the comparison revision is no longer current; prepare the comparison again");
            }
        }
        finally
        {
            _gate.Release();
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        ComparisonReply comparison;
        if (_comparisonRunner is null)
        {
            var failure = new ComparisonSide("setup-failed", [], null, null, "", "",
                "this frontend has no isolated comparison runner");
            comparison = new ComparisonReply(package.Name, package.BaselineFingerprint, package.Revision,
                "incomplete", package.StartingState, failure, failure);
        }
        else
        {
            comparison = await _comparisonRunner(package, cancellation.Token).ConfigureAwait(false);
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!_core.Session.Edits.Contains(capturedEdit) || capturedEdit.Revision != package.Revision)
            {
                throw new ReplEngineException("the comparison revision changed during execution; prepare the comparison again");
            }

            _core.ReportComparison(comparison);
            return Reply(new HandleResult(!package.Assert || comparison.Outcome == "match", false)) with
            {
                Comparison = comparison,
            };
        }
        finally
        {
            _gate.Release();
        }
    }
}
