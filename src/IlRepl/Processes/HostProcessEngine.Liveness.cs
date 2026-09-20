using System.Diagnostics;
using StreamJsonRpc;

namespace IlRepl.Processes;

/// <summary>
/// Ends a host that Windows keeps after a fatal error, instead of waiting until the operating system lets it go.
/// </summary>
/// <remarks>
/// After a fatal error the runtime writes its report and hands the process to Windows Error Reporting, which holds it while
/// it walks the failing thread. For a stack overflow on the 8 MB execution thread that takes over half a minute, and the
/// prompt would wait all that time to say what happened. Such a process cannot answer, and ending it takes no time.
/// </remarks>
public sealed partial class HostProcessEngine
{
    private static readonly TimeSpan s_quiet = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan s_silence = TimeSpan.FromSeconds(3);
    private readonly CancellationTokenSource _liveness = new();
    private long _diagnosed;
    private int _checking;

    private void WatchDiagnostics()
    {
        if (OperatingSystem.IsWindows())
        {
            _stderr.Appended += OnDiagnostics;
        }
    }

    private async Task StopWatchingDiagnosticsAsync()
    {
        _stderr.Appended -= OnDiagnostics;
        await _liveness.CancelAsync().ConfigureAwait(false);
    }

    private void OnDiagnostics()
    {
        Volatile.Write(ref _diagnosed, Stopwatch.GetTimestamp());
        if (Interlocked.Exchange(ref _checking, 1) == 0)
        {
            _ = CheckLivenessAsync();
        }
    }

    // A healthy host can write diagnostics too, so text alone decides nothing. Only a host that then cannot answer is ended.
    private async Task CheckLivenessAsync()
    {
        try
        {
            while (true)
            {
                while (Stopwatch.GetElapsedTime(Volatile.Read(ref _diagnosed)) < s_quiet)
                {
                    await Task.Delay(s_quiet, _liveness.Token).ConfigureAwait(false);
                }

                var seen = Volatile.Read(ref _diagnosed);
                if (!await AnswersAsync().ConfigureAwait(false))
                {
                    await _lifetime.StopAsync(_scope.Identity, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                Interlocked.Exchange(ref _checking, 0);
                if (Volatile.Read(ref _diagnosed) == seen || Interlocked.Exchange(ref _checking, 1) != 0)
                {
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // Disposal is ending the host already.
        }
    }

    private async Task<bool> AnswersAsync()
    {
        try
        {
            // No operation has an empty identity, so this interrupts nothing and returns as soon as the host can answer at all.
            // The wait ends here and not through the call's token, because a cancelled call waits for the host to confirm it.
            await _host.InterruptAsync("", CancellationToken.None).WaitAsync(s_silence, _liveness.Token).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return _exit.Task.IsCompleted;
        }
        catch (Exception exception) when (exception is ConnectionLostException or RemoteInvocationException)
        {
            // The connection is gone, and the exit is observed where it always is.
            return true;
        }
    }
}
