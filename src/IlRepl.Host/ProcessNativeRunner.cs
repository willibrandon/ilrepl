using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text;
using System.Text.Json;
using IlRepl.Engine;
using IlRepl.Protocol;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace IlRepl.Host;

/// <summary>
/// Captures actual CoreCLR compilations in disposable workers with matching comparison settings.
/// </summary>
public static class ProcessNativeRunner
{
    /// <summary>
    /// Inspects one target or two sequential fresh workers using the same restored fixture path.
    /// </summary>
    /// <param name="package">The prepared immutable request.</param>
    /// <param name="cancellationToken">Cancels the inspection and terminates its process group.</param>
    /// <returns>The available native evidence and comparison outcome.</returns>
    public static Task<NativeReply> RunAsync(NativePackage package, CancellationToken cancellationToken) =>
        RunAsync(package, null, cancellationToken);

    /// <summary>
    /// Registers each prepared worker with frontend ownership before allowing user execution.
    /// </summary>
    /// <param name="package">The prepared immutable request.</param>
    /// <param name="register">The frontend ownership acknowledgement.</param>
    /// <param name="cancellationToken">Cancels execution and ownership registration.</param>
    /// <returns>The resulting worker observations.</returns>
    internal static async Task<NativeReply> RunAsync(
        NativePackage package,
        Func<OwnedProcessScope, CancellationToken, Task>? register,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();
        var capabilityKey = package.Options.Info ? NativeCapabilityCache.Key(package) : null;
        if (capabilityKey is not null && NativeCapabilityCache.TryGet(capabilityKey, out var cached))
        {
            return cached;
        }

        var root = Path.Combine(Path.GetTempPath(), "ilrepl-native-" + Guid.NewGuid().ToString("N"));
        var left = await RunSideAsync(package, true, root, register, cancellationToken).ConfigureAwait(false);
        if (package.Right is null)
        {
            var reply = new NativeReply { Outcome = left.Outcome, Left = left };
            if (capabilityKey is not null)
            {
                NativeCapabilityCache.Store(capabilityKey, reply);
            }

            return reply;
        }

        var right = await RunSideAsync(package, false, root, register, cancellationToken).ConfigureAwait(false);
        if (left.Outcome != "complete" || right.Outcome != "complete")
        {
            return new NativeReply { Outcome = "incomplete", Left = left, Right = right };
        }

        if (!package.Options.Raw && (left.NormalizationProblems.Length != 0 || right.NormalizationProblems.Length != 0))
        {
            return new NativeReply { Outcome = "indeterminate", Left = left, Right = right };
        }

        // An address load is compared as one step, because its length in instructions follows the address and not the code.
        // A call through a loaded cell address is compared as the direct call, because only the distance to the cell decides it.
        var leftLines = NativeCellCalls.Fold(NativeAddressLoads.Fold(left.Compilations[^1].Normalized));
        var rightLines = NativeCellCalls.Fold(NativeAddressLoads.Fold(right.Compilations[^1].Normalized));
        var equal = leftLines.SequenceEqual(rightLines, StringComparer.Ordinal);
        return new NativeReply
        {
            Outcome = equal ? "equal" : "different", Left = left, Right = right,
            Difference = equal ? [] : NativeDifference.Create(leftLines, rightLines, left.Name, right.Name),
        };
    }

    private static async Task<NativeReport> RunSideAsync(
        NativePackage package,
        bool left,
        string root,
        Func<OwnedProcessScope, CancellationToken, Task>? register,
        CancellationToken cancellationToken)
    {
        var target = left ? package.Left : package.Right!;
        var state = new NativeWorkerState
        {
            Report = new NativeReport
            {
                Name = target.Name,
                Fingerprint = target.Fingerprint,
                IsCapability = package.Options.Info,
                Raw = package.Options.Raw,
            },
        };

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var outputLifetime = new CancellationTokenSource();
        using var process = new Process();
        using var group = new OwnedProcessGroup();
        using var port = new NativeDiagnosticPort();
        DiagnosticsClientConnector? connector = null;
        EventPipeSession? session = null;
        EventPipeEventSource? source = null;
        NativeEventCollector? collector = null;
        Task? tracing = null;
        Task<DiagnosticsClientConnector>? connecting = null;
        Task? input = null;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        var started = false;
        var listingPath = Path.Combine(root, "native.txt");
        var overflow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = "complete";
        string? detail = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ComparisonDirectory.Delete(root);
            ComparisonDirectory.Create(root);
            Directory.CreateDirectory(Path.Combine(root, "work"));
            var packagePath = Path.Combine(root, "package.json");
            await File.WriteAllTextAsync(packagePath, JsonSerializer.Serialize(package, ProtocolJsonContext.Default.NativePackage),
                cancellationToken).ConfigureAwait(false);
            var environment = NativeRuntimeSettings.Create(package, target, listingPath, port.Address);
            var bundled = Path.Combine(AppContext.BaseDirectory, "host", "ilrepl-host.dll");
            var host = File.Exists(bundled) ? bundled : typeof(ProcessNativeRunner).Assembly.Location;
            process.StartInfo = new ProcessStartInfo
            {
                FileName = RuntimeHost(), WorkingDirectory = Path.Combine(root, "work"),
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true,
            };

            process.StartInfo.ArgumentList.Add("--fx-version");
            process.StartInfo.ArgumentList.Add(NativeRuntimeSettings.FrameworkVersion(typeof(object).Assembly.Location));
            process.StartInfo.ArgumentList.Add("--roll-forward");
            process.StartInfo.ArgumentList.Add("Disable");
            foreach (var argument in new[] { host, "--native-worker", packagePath, left ? "left" : "right", root })
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.StartInfo.Environment.Clear();
            foreach (var (key, value) in environment)
            {
                process.StartInfo.Environment[key] = value;
            }

            lifetime.CancelAfter(TimeSpan.FromMinutes(2));
            connecting = DiagnosticsClientConnector.FromDiagnosticPort(port.Address + ",listen", lifetime.Token);
            WorkerOwnerWatchdog.Configure(process.StartInfo);
            started = process.Start();
            if (!started)
            {
                throw new IOException("could not start the native inspection runtime");
            }

            input = WriteInputAsync(process.StandardInput.BaseStream, package.Options.StandardInput, lifetime.Token);
            stdout = ReadOutputAsync(process.StandardOutput, 64 * 1024, overflow, outputLifetime.Token,
                NativeOutputBuffer.StartMarker(root));
            stderr = ReadOutputAsync(process.StandardError, 64 * 1024, overflow, outputLifetime.Token);
            var exit = OwnedProcessGroup.WaitForExitAsync(process, CancellationToken.None);
            if (await Task.WhenAny(connecting, exit).ConfigureAwait(false) == exit)
            {
                throw new IOException($"native runtime exited during diagnostics startup with code {process.ExitCode}");
            }

            connector = await connecting.ConfigureAwait(false) ?? throw new IOException("CoreCLR diagnostics startup timed out");
            // Windows' default pipe avoids reverse-pipe reconnection delays between start, resume, and stop commands.
            var diagnostics = OperatingSystem.IsWindows() ? new DiagnosticsClient(process.Id) : connector.Instance;
            session = await diagnostics.StartEventPipeSessionAsync(
                [new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose,
                    (long)(ClrTraceEventParser.Keywords.Jit | ClrTraceEventParser.Keywords.JitTracing
                        | ClrTraceEventParser.Keywords.Loader | ClrTraceEventParser.Keywords.JittedMethodILToNativeMap))],
                            requestRundown: false, circularBufferMB: 32,
                token: lifetime.Token).ConfigureAwait(false);
            source = new EventPipeEventSource(session.EventStream);
            collector = new NativeEventCollector(source);
            // TraceEvent blocks while reading its stream; do not occupy a pool thread needed by concurrent workers or the terminal.
            tracing = Task.Factory.StartNew(() => source.Process(), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            // The shipping diagnostics client exposes only a synchronous resume call, including its pipe handshake.
            await Task.Factory.StartNew(connector.Instance.ResumeRuntime, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
            var attached = false;
            var ready = false;
            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (!attached && File.Exists(Path.Combine(root, "group-ready")))
                {
                    group.Attach(process);
                if (register is not null)
                    {
                        await register(OwnedProcessGroup.Describe(process, Guid.NewGuid().ToString("N")), cancellationToken)
                        .ConfigureAwait(false);
                    }

                    attached = true;
                    await File.WriteAllTextAsync(Path.Combine(root, "start"), "start", lifetime.Token).ConfigureAwait(false);
                }

                if (!ready && File.Exists(Path.Combine(root, "ready")))
                {
                    ready = true;
                    lifetime.CancelAfter(package.Options.TimeoutMilliseconds);
                }

                var statePath = Path.Combine(root, "state.json");
                state = await NativeStateFile.ReadAsync(statePath, lifetime.Token).ConfigureAwait(false) ?? state;
                var observed = state.MethodId != 0 && collector.Observed(state.MethodId, state.Method, package.Options.Tier);
                if (observed && package.Options.Tier == "tier1")
                {
                    await File.WriteAllTextAsync(Path.Combine(root, "stop"), "stop", lifetime.Token).ConfigureAwait(false);
                }

                if (overflow.Task.IsCompleted)
                {
                    outcome = "output-limit";
                    detail = "worker output exceeded 64 KiB";
                    break;
                }

                if (File.Exists(listingPath) && new FileInfo(listingPath).Length > 16 * 1024 * 1024)
                {
                    outcome = "listing-limit";
                    detail = "JIT output exceeded 16 MiB";
                    break;
                }

                if (File.Exists(Path.Combine(root, "work-done"))
                    && (package.Options.Tier != "tier1" || observed || state.Report.Outcome != "complete"))
                {
                    break;
                }

                if (exit.IsCompleted)
                {
                    outcome = "crashed";
                    detail = $"native runtime exited with code {process.ExitCode}";
                    break;
                }

                if (tracing.IsFaulted)
                {
                    await tracing.ConfigureAwait(false);
                }

                await Task.Delay(10, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            outcome = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
            detail = cancellationToken.IsCancellationRequested ? "native inspection cancelled"
                : "requested tier was not observed before the deadline; retained available compilations and invocation count";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or Win32Exception or JsonException or ReplException or DiagnosticsClientException or FormatException)
        {
            outcome = "failed";
            detail = exception.Message;
        }
        finally
        {
            if (session is not null && started && !process.HasExited)
            {
                var draining = false;
                try
                {
                    var finished = outcome == "complete" && File.Exists(Path.Combine(root, "work-done"));
                    using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(finished ? 30 : 5));
                    await session.StopAsync(drain.Token).ConfigureAwait(false);
                    draining = true;
                    if (tracing is not null)
                    {
                        await tracing.WaitAsync(drain.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException
                    or DiagnosticsClientException or FormatException)
                {
                    outcome = "incomplete";
                    detail ??= (draining ? "the runtime event stream could not be drained: "
                        : "the runtime event session could not be stopped: ") + exception.Message;
                }
            }

            await lifetime.CancelAsync().ConfigureAwait(false);
            if (started && !process.HasExited && File.Exists(Path.Combine(root, "work-done")))
            {
                // Orderly shutdown also flushes release runtimes that leave the final native listing buffered.
                await File.WriteAllTextAsync(Path.Combine(root, "release"), "release", CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await OwnedProcessGroup.WaitForExitAsync(process, CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // A workload can register an exit handler that never returns.
                }
            }

            Kill(process);
            if (started)
            {
                await OwnedProcessGroup.WaitForExitAsync(process, CancellationToken.None).ConfigureAwait(false);
            }

            await group.StopAsync().ConfigureAwait(false);
            outputLifetime.CancelAfter(TimeSpan.FromSeconds(1));
            if (input is not null)
            {
                await input.ConfigureAwait(false);
            }

            if (connector is null && connecting is not null)
            {
                try
                {
                    connector = await connecting.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The reverse endpoint was cancelled during startup.
                }
            }

            source?.Dispose();
            session?.Dispose();
            if (connector is not null)
            {
                await connector.DisposeAsync().ConfigureAwait(false);
            }
        }

        var finalStatePath = Path.Combine(root, "state.json");
        state = await NativeStateFile.ReadAsync(finalStatePath, CancellationToken.None).ConfigureAwait(false) ?? state;
        var report = state.Report with
        {
            Outcome = outcome == "complete" ? state.Report.Outcome : outcome, Detail = detail ?? state.Report.Detail,
            StandardOutput = stdout is null ? "" : await stdout.ConfigureAwait(false),
            StandardError = stderr is null ? "" : await stderr.ConfigureAwait(false),
        };

        var raw = File.Exists(listingPath) ? await ReadListingAsync(listingPath).ConfigureAwait(false) : "";
        var events = collector?.Snapshot() ?? [];
        var publications = events.Where(item => NativeEventCollector.Selected(item, state.MethodId, state.Method)).ToList();
        var compilations = new List<NativeCompilation>();
        var blocks = NativeDisassembly.Parse(raw);
        var addresses = state.Report.Addresses.Concat(events.Select(item => new NativeAddressFact
        {
            Address = item.Compilation.Address, Length = (ulong)item.Compilation.CodeSize, Kind = "code",
            Symbol = item.Compilation.Method, Evidence = "EventPipe MethodLoadVerbose published code range",
        })).ToArray();

        var normalizationProblems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            if (state.Method is null || !state.Method.JitNames.Contains(block.Method, StringComparer.Ordinal))
            {
                continue;
            }

            var matches = publications.Where(item => item.Compilation.CodeSize == block.CodeSize
                && SameTier(item.Compilation.Tier, block.Tier)).ToArray();
            if (matches.Length != 1)
            {
                continue;
            }

            var published = matches[0].Compilation;
            publications.Remove(matches[0]);
            var normalized = NativeNormalizer.Normalize(block with { Address = published.Address }, addresses, state.Probes, blocks,
                report.Architecture, report.Constants, state.Method.ReturnsPointer);
            normalizationProblems.UnionWith(normalized.Problems);
            addresses = normalized.Addresses;
            compilations.Add(block with
            {
                MethodId = published.MethodId, CodeVersion = published.CodeVersion, Address = published.Address,
                Inlinees = published.Inlinees, Normalized = package.Options.Raw ? NativeDisassembly.Instructions(block,
                    true) : normalized.Lines,
            });
        }

        report = report with
        {
            Compilations = [.. compilations], Addresses = addresses, NormalizationProblems = [.. normalizationProblems],
            UnattributedListings = NativeDisassembly.Unattributed(raw, state.Method?.JitNames ?? [], compilations),
        };

        if (report.UnattributedListings.Length != 0 && report.Outcome == "complete")
        {
            report = report with { Outcome = "incomplete", Detail = "selected-signature output lacks complete, unambiguous attribution" };
        }

        if (compilations.Count == 0 && report.Outcome == "complete")
        {
            report = report with
            {
                Outcome = "incomplete",
                Detail = "no complete listing could be attributed to the selected runtime method "
                    + $"({blocks.Length} listings; {publications.Count} matching publications)",
            };
        }

        if (report.Outcome == "complete" && package.Options.Tier is "tier0" or "tier1"
            && !compilations.Any(compilation => package.Options.Tier == "tier0"
                ? compilation.Tier is "Tier0" or "Instrumented Tier0" : compilation.Tier is "Tier1" or "Instrumented Tier1" or "OSR"))
        {
            report = report with
            {
                Outcome = "tier-unavailable",
                Detail = "the requested tier was not observed; retained actual compilations",
            };
        }

        if (source?.EventsLost > 0)
        {
            report = report with { Outcome = "incomplete", Detail = "runtime diagnostic events were lost" };
        }

        try
        {
            ComparisonDirectory.Delete(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return report;
    }

    private static string RuntimeHost()
    {
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var root = Directory.GetParent(runtime)?.Parent?.Parent?.FullName;
        var host = root is null ? "" : Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (!File.Exists(host))
        {
            throw new ReplException("cannot locate the dotnet host for this exact CoreCLR installation");
        }

        return host;
    }

    private static bool SameTier(string actual, string header) => actual == header
        || actual == "OSR" && header.Contains("OSR", StringComparison.Ordinal);

    private static async Task<string> ReadListingAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[(int)Math.Min(stream.Length, 16 * 1024 * 1024)];
        var count = await stream.ReadAtLeastAsync(buffer.AsMemory(), buffer.Length, throwOnEndOfStream: false).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer, 0, count);
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

    private static async Task<string> ReadOutputAsync(
        StreamReader reader,
        int limit,
        TaskCompletionSource overflow,
        CancellationToken cancellationToken,
        string? startMarker = null)
    {
        using var capturedOutput = reader;
        var output = new NativeOutputBuffer(limit, startMarker);
        var buffer = new byte[4096];
        try
        {
            while (await capturedOutput.BaseStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)
                is var count && count != 0)
            {
                output.Append(buffer.AsSpan(0, count));
                if (output.Overflowed)
                {
                    overflow.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The worker has exited; retain its captured prefix if an inherited pipe stayed open.
        }

        return output.Text;
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
}
