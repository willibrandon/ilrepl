using IlRepl.Protocol;

namespace IlRepl.Repl;

/// <summary>
/// Runs one-use native tickets outside the live session gate and rejects stale snapshots.
/// </summary>
public sealed partial class InProcessEngine
{
    /// <inheritdoc/>
    public Task<HandleReply> InspectNativeAsync(string identity, CancellationToken cancellationToken)
    {
        Task<HandleReply> inspection;
        lock (_analysisLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            inspection = InspectNativeCoreAsync(identity, cancellationToken);
            _analyses.Add(inspection);
        }

        _ = inspection.ContinueWith(RemoveAnalysis, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return inspection;
    }

    private async Task<HandleReply> InspectNativeCoreAsync(string identity, CancellationToken cancellationToken)
    {
        NativePackage package;
        long revision;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_preparedNative is not { } prepared || prepared.Ticket.Identity != identity)
            {
                throw new ReplEngineException("the native ticket is no longer available; prepare the inspection again");
            }

            _preparedNative = null;
            revision = prepared.Revision;
            package = prepared.Package;
            if (revision != _core.Status.Revision)
            {
                throw new ReplEngineException("the session changed; prepare the native inspection again");
            }
        }
        finally
        {
            _gate.Release();
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var result = _nativeRunner is null
            ? new NativeReply
            {
                Left = new NativeReport
                    {
                        Name = package.Left.Name, Detail = "this frontend has no isolated CoreCLR native inspection worker",
                    },
            }
            : await _nativeRunner(package, cancellation.Token).ConfigureAwait(false);

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (revision != _core.Status.Revision)
            {
                throw new ReplEngineException("the session changed during native inspection; prepare it again");
            }

            _core.ReportNative(result);
            var succeeded = result.Outcome is "complete" or "equal" or "different"
                && (!package.Options.Assert || result.Outcome == "equal");
            return Reply(new HandleResult(succeeded, false)) with { Native = result };
        }
        finally
        {
            _gate.Release();
        }
    }
}
