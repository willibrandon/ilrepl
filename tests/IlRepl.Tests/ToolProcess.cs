using System.Diagnostics;
using IlRepl.Protocol;

namespace IlRepl.Tests;

/// <summary>
/// Drains real tool processes concurrently and guarantees termination when cancellation or a deadline expires.
/// </summary>
internal static class ToolProcess
{
    /// <summary>
    /// Runs a tool with redirected output, bounded duration, and deterministic process cleanup.
    /// </summary>
    /// <param name="start">The tool executable, arguments, environment, and working directory.</param>
    /// <param name="cancellationToken">Cancels the process and all descendants.</param>
    /// <returns>The actual process status and both output streams.</returns>
    internal static async Task<ToolResult> RunAsync(ProcessStartInfo start, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The conformance tool did not start.");
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await OwnedProcessGroup.WaitForExitAsync(process, deadline.Token);
            await Task.WhenAll(output, error).WaitAsync(deadline.Token);
            return new ToolResult(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The conformance tool exceeded its 30 second deadline: " + start.FileName);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await OwnedProcessGroup.WaitForExitAsync(process, CancellationToken.None);

            await deadline.CancelAsync();
            try
            {
                await Task.WhenAll(output, error);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                // A descendant retaining a pipe must not keep cleanup alive beyond the operation deadline.
            }
        }
    }
}
