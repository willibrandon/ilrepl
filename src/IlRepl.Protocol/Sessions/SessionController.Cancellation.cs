namespace IlRepl.Protocol;

/// <summary>
/// Cancels explicit replay while preserving the complete experiment in a fresh inactive runtime.
/// </summary>
public sealed partial class SessionController
{
    private readonly Lock _executionLock = new();
    private CancellationTokenSource? _executionCancellation;

    /// <summary>
    /// Interrupts an explicit session run without withdrawing previously accepted source.
    /// </summary>
    public void CancelExecution()
    {
        lock (_executionLock)
        {
            _executionCancellation?.Cancel();
        }
    }

    private async Task<(IReplEngine Engine, SessionReply Reply)> RunCandidateAsync(IReplEngine candidate, SessionRequest request,
        CancellationToken cancellationToken)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_executionLock)
        {
            _executionCancellation = execution;
        }

        try
        {
            return (candidate, await candidate.SessionAsync(request, execution.Token).WaitAsync(execution.Token)
                .ConfigureAwait(false));
        }
        catch (OperationCanceledException)
            when (execution.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_disposed)
        {
            await candidate.DisposeAsync().ConfigureAwait(false);
            var source = request.Document!;
            var interruption = new SessionInterruption
            {
                Number = request.Action.Numbers.Length != 0 ? request.Action.Numbers.Min()
                    : source.Cells.FirstOrDefault(cell => cell.Kind == "cell")?.Number ?? Status.CellNumber,
                Source = [".session run" + (request.Action.Numbers.Length == 0 ? "" : " "
                    + string.Join(' ', request.Action.Numbers.Order()))],
            };
            var recovered = await _start(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!Status.Mark.EchoStack)
                {
                    await recovered.HandleAsync(".quiet on", cancellationToken).ConfigureAwait(false);
                }

                if (Status.Mark.ShowTiming)
                {
                    await recovered.HandleAsync(".time on", cancellationToken).ConfigureAwait(false);
                }

                var reply = await recovered.SessionAsync(new SessionRequest
                {
                    Action = new SessionAction { Operation = SessionOperation.Hydrate, Path = request.Action.Path },
                    Document = source with { Interruptions = [.. source.Interruptions, interruption] },
                    Editor = source.Editor, Modified = true,
                }, cancellationToken).ConfigureAwait(false);
                return (recovered, reply with { Reply = reply.Reply with { Succeeded = false, Lines = [.. reply.Reply.Lines,
                    TranscriptLine.Of(LineKind.Info, "  session run cancelled; all source remains available", SpanStyle.Dim)] } });
            }
            catch
            {
                await recovered.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            lock (_executionLock)
            {
                _executionCancellation = null;
            }
        }
    }
}
