using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Host;

/// <summary>
/// Runs each captured version in a separate host process with a fresh fixture tree at the same working path.
/// </summary>
public static class ProcessComparisonRunner
{
    /// <summary>
    /// Executes two independent runtime snapshots and compares their typed observations.
    /// </summary>
    /// <param name="package">The captured versions and starting conditions.</param>
    /// <param name="cancellationToken">Terminates workers and cancels the remaining execution.</param>
    /// <returns>The observations and comparison outcome.</returns>
    public static async Task<ComparisonReply> RunAsync(ComparisonPackage package, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        var path = Path.Combine(Path.GetTempPath(), "ilrepl-compare-" + Guid.NewGuid().ToString("N"));
        var original = await RunSideAsync(package, true, path, cancellationToken).ConfigureAwait(false);
        var edited = await RunSideAsync(package, false, path, cancellationToken).ConfigureAwait(false);
        return ComparisonResults.Compare(package, original, edited);
    }

    private static async Task<ComparisonSide> RunSideAsync(ComparisonPackage package, bool original, string path,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Failure("cancelled", "comparison cancelled");
        }

        var directory = new DirectoryInfo(path);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var inputLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var outputLifetime = new CancellationTokenSource();
        using var process = new Process();
        using var group = new ComparisonProcessGroup();
        var processStarted = false;
        Task? stdin = null;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        try
        {
            // Failed cleanup must prevent the next worker from inheriting changed fixtures or stale control files.
            ComparisonDirectory.Delete(path);
            ComparisonDirectory.Create(path);
            var work = Directory.CreateDirectory(Path.Combine(directory.FullName, "work"));
            var packagePath = Path.Combine(directory.FullName, "package.json");
            var readyPath = Path.Combine(directory.FullName, "ready");
            var resultPath = Path.Combine(directory.FullName, "result.json");
            var limitPath = Path.Combine(directory.FullName, "output-limit");
            var groupPath = Path.Combine(directory.FullName, "group-ready");
            var startPath = Path.Combine(directory.FullName, "start");
            var resultReadyPath = Path.Combine(directory.FullName, "result-ready");
            await File.WriteAllTextAsync(packagePath, JsonSerializer.Serialize(package, ProtocolJsonContext.Default.ComparisonPackage),
                cancellationToken).ConfigureAwait(false);
            var bundled = Path.Combine(AppContext.BaseDirectory, "host", "ilrepl-host.dll");
            var host = File.Exists(bundled) ? bundled : typeof(ProcessComparisonRunner).Assembly.Location;
            process.StartInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                WorkingDirectory = work.FullName,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add(host);
            process.StartInfo.ArgumentList.Add("--comparison-worker");
            process.StartInfo.ArgumentList.Add(packagePath);
            process.StartInfo.ArgumentList.Add(original ? "original" : "edited");
            process.StartInfo.ArgumentList.Add(readyPath);
            process.StartInfo.ArgumentList.Add(resultPath);
            process.StartInfo.ArgumentList.Add(limitPath);
            process.StartInfo.ArgumentList.Add(groupPath);
            process.StartInfo.ArgumentList.Add(startPath);
            process.StartInfo.ArgumentList.Add(resultReadyPath);
            process.StartInfo.Environment.Clear();
            foreach (var pair in package.Environment)
            {
                process.StartInfo.Environment[pair.Key] = pair.Value;
            }

            processStarted = process.Start();
            if (!processStarted)
            {
                return Failure("setup-failed", "could not start a comparison host");
            }

            stdin = WriteInputAsync(process.StandardInput.BaseStream, package.StandardInput, inputLifetime.Token);
            var overflow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            stdout = ReadOutputAsync(process.StandardOutput, package.OutputLimit, overflow, outputLifetime.Token);
            stderr = ReadOutputAsync(process.StandardError, package.OutputLimit, overflow, outputLifetime.Token);
            var exit = process.WaitForExitAsync(CancellationToken.None);
            var ready = WaitForReadyAsync(readyPath, lifetime.Token);
            var resultReady = WaitForReadyAsync(resultReadyPath, lifetime.Token);
            var startup = Task.Delay(TimeSpan.FromMinutes(2), lifetime.Token);
            var prepared = WaitForReadyAsync(groupPath, lifetime.Token);
            var preparation = await Task.WhenAny(exit, prepared, startup, overflow.Task).ConfigureAwait(false);
            if (preparation == prepared && !prepared.IsCanceled)
            {
                group.Attach(process);
                await File.WriteAllTextAsync(startPath, "start", cancellationToken).ConfigureAwait(false);
            }

            var started = await Task.WhenAny(exit, ready, resultReady, startup, overflow.Task).ConfigureAwait(false);
            if (started == startup || cancellationToken.IsCancellationRequested)
            {
                return await StopAsync(cancellationToken.IsCancellationRequested ? "cancelled" : "setup-failed",
                    cancellationToken.IsCancellationRequested ? "comparison cancelled" : "comparison runtime startup timed out")
                    .ConfigureAwait(false);
            }

            if (started == resultReady && !resultReady.IsCanceled)
            {
                Kill(process);
                await exit.ConfigureAwait(false);
                await group.StopAsync().ConfigureAwait(false);
            }
            else if (started == ready && !ready.IsCanceled)
            {
                var timeout = Task.Delay(package.TimeoutMilliseconds, lifetime.Token);
                var completed = await Task.WhenAny(exit, resultReady, timeout, overflow.Task).ConfigureAwait(false);
                if (completed == resultReady && !resultReady.IsCanceled)
                {
                    Kill(process);
                    await exit.ConfigureAwait(false);
                    await group.StopAsync().ConfigureAwait(false);
                }
                else if (completed != exit)
                {
                    return await StopAsync(cancellationToken.IsCancellationRequested ? "cancelled"
                        : completed == overflow.Task ? "output-limit"
                        : "timeout",
                        cancellationToken.IsCancellationRequested ? "comparison cancelled" : completed == overflow.Task
                            ? "worker output exceeded the configured limit"
                                : $"execution exceeded {package.TimeoutMilliseconds} ms after runtime startup").ConfigureAwait(false);
                }
            }
            else if (started == overflow.Task)
            {
                Kill(process);
                await exit.ConfigureAwait(false);
                await group.StopAsync().ConfigureAwait(false);
            }

            await exit.ConfigureAwait(false);
            await group.StopAsync().ConfigureAwait(false);
            outputLifetime.CancelAfter(TimeSpan.FromSeconds(1));
            var rawOut = await stdout.ConfigureAwait(false);
            var rawError = await stderr.ConfigureAwait(false);
            if (File.Exists(limitPath) || overflow.Task.IsCompleted)
            {
                return Failure("output-limit", "worker output exceeded the configured limit", rawOut, rawError);
            }

            if (!File.Exists(resultReadyPath) && (process.ExitCode != 0 || !File.Exists(resultPath)))
            {
                return Failure("crashed", $"comparison host exited with code {process.ExitCode}", rawOut, rawError);
            }

            var result = JsonSerializer.Deserialize(await File.ReadAllTextAsync(resultPath, cancellationToken).ConfigureAwait(false),
                ProtocolJsonContext.Default.ComparisonSide)
                ?? throw new InvalidDataException("comparison host returned no result");
            return result with { StandardOutput = rawOut, StandardError = rawError };
        }
        catch (OperationCanceledException)
        {
            return await StopAsync("cancelled", "comparison cancelled").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or Win32Exception or JsonException)
        {
            return await StopAsync("setup-failed", ex.Message).ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await inputLifetime.CancelAsync().ConfigureAwait(false);
            Kill(process);
            await group.StopAsync().ConfigureAwait(false);
            if (processStarted)
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (stdin is not null)
            {
                await stdin.ConfigureAwait(false);
            }

            await outputLifetime.CancelAsync().ConfigureAwait(false);
            if (stdout is not null)
            {
                await stdout.ConfigureAwait(false);
            }

            if (stderr is not null)
            {
                await stderr.ConfigureAwait(false);
            }

            try
            {
                ComparisonDirectory.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // User code can leave files or directories that cannot be deleted; retain the comparison outcome.
            }
        }

        async Task<ComparisonSide> StopAsync(string outcome, string detail)
        {
            Kill(process);
            await group.StopAsync().ConfigureAwait(false);
            if (processStarted)
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            // Drain closed pipes without waiting indefinitely for descendants that retained inherited handles.
            outputLifetime.CancelAfter(TimeSpan.FromSeconds(1));
            var rawOut = stdout is null ? "" : await stdout.ConfigureAwait(false);
            var rawError = stderr is null ? "" : await stderr.ConfigureAwait(false);
            return Failure(outcome, detail, rawOut, rawError);
        }
    }

    private static async Task WaitForReadyAsync(string path, CancellationToken cancellationToken)
    {
        while (!File.Exists(path))
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteInputAsync(Stream stream, string input, CancellationToken cancellationToken)
    {
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(input), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The comparison ended before the worker consumed all input.
            }
            catch (IOException)
            {
                // The worker may exit or close stdin without consuming the remaining input.
            }
        }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, int limit, TaskCompletionSource overflow,
        CancellationToken cancellationToken)
    {
        using var capturedOutput = reader;
        var text = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            while (await capturedOutput.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) is var count && count != 0)
            {
                var remaining = Math.Max(0, limit - text.Length);
                text.Append(buffer, 0, Math.Min(count, remaining));
                if (count > remaining)
                {
                    overflow.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The worker has exited; retain its captured prefix if an inherited pipe stayed open.
        }

        return text.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process either has not started or already exited.
        }
    }

    private static ComparisonSide Failure(string outcome, string detail, string stdout = "", string stderr = "")
        => new(outcome, [], null, null, stdout, stderr, detail);
}
