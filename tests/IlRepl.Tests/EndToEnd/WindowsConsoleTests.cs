using System.Diagnostics;
using Hex1b;
using Hex1b.Automation;
using IlRepl.Engine;
using IlRepl.Processes;
using IlRepl.Protocol;
using IlRepl.Tests.Engine;
using System.Text;

namespace IlRepl.Tests.EndToEnd;

/// <summary>
/// Exercises native Windows console interruption and closure against real frontends, hosts, and orphaned descendants.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class WindowsConsoleTests
{
    private static readonly string[] s_recordSuffixes = [".launcher", "", ".exit", ".error", ".stderr"];

    /// <summary>
    /// Supplies cancellation for terminal, process, and filesystem synchronization.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Ctrl+Break flushes partial batch output, exits 130, and terminates the runtime and its orphaned grandchild.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public Task CtrlBreak_Exits130AndStopsDescendants() => RunAsync(close: false);

    /// <summary>
    /// Closing a real ConPTY sends CTRL_CLOSE_EVENT and leaves no frontend, runtime, or orphaned grandchild running.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public Task ConsoleClose_StopsFrontendAndDescendants() => RunAsync(close: true);

    private async Task RunAsync(bool close)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var frontendRecord = Path.Join(files.DirectoryPath, "frontend.pid");
        var hostRecord = Path.Join(files.DirectoryPath, "host.pid");
        var closeMarker = Path.Join(files.DirectoryPath, "console.closed");
        var descendants = Path.Join(files.DirectoryPath, "processes");
        var descendantsReady = Path.Join(files.DirectoryPath, "descendants.ready");
        var script = Path.Join(files.DirectoryPath, "descendants.il");
        string[] source =
        [
            ".load " + LiteralParser.Escape(typeof(ComparisonDescendantSource).Assembly.Location),
            "ldstr " + LiteralParser.Escape(hostRecord), "call int32 Environment::get_ProcessId()", "box int32",
            "callvirt instance string Object::ToString()", "call void File::WriteAllText(string, string)",
            "ldstr \"batch partial output\"", "call void Console::WriteLine(string)",
            "ldstr " + LiteralParser.Escape(Environment.ProcessPath!), "ldstr " + LiteralParser.Escape(files.DirectoryPath),
            "ldc.i4.1", "ldstr \"return\"", "ldc.i4.1",
            "call int32 [IlRepl.Tests]IlRepl.Tests.Engine.ComparisonDescendantSource::Run(string, string, bool, string, bool)",
            "pop", "ldstr " + LiteralParser.Escape(descendantsReady + ".pending"), "ldstr \"ready\"",
            "call void File::WriteAllText(string, string)", "ldstr " + LiteralParser.Escape(descendantsReady + ".pending"),
            "ldstr " + LiteralParser.Escape(descendantsReady), "call void File::Move(string, string)",
            "LOOP: br LOOP", "ret",
        ];
        await File.WriteAllLinesAsync(script, source, token);
        var recorder = new WorkloadRecorder();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithPtyProcess(options =>
        {
            options.FileName = HostLocator.FindDotnet();
            // The direct backend closes ConPTY itself, without a proxy process-tree kill racing the close event.
            options.WindowsPtyMode = WindowsPtyMode.Direct;
            options.Arguments = [typeof(WindowsConsoleProbe).Assembly.Location, "--windows-console", "launch",
                frontendRecord, RepoPaths.FrontEndAssembly, "--no-color", script];
            options.WorkingDirectory = files.DirectoryPath;
        }).AddWorkloadFilter(recorder).WithHeadless().WithDimensions(100, 30).Build();

        // The run ends when the PTY process is seen to exit or this token is cancelled. Closing the console disposes the
        // terminal, which releases that process, so the close case ends the run itself instead of waiting to see the exit.
        using var running = CancellationTokenSource.CreateLinkedTokenSource(token);
        var run = terminal.RunAsync(running.Token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(20));
        Process? frontend = null;
        Process? host = null;
        Process? observer = null;
        try
        {
            await auto.WaitUntilAsync(_ => run.IsCompleted || File.Exists(frontendRecord + ".error") || IsReady());
            Assert.IsTrue(IsReady(), "The real batch fixture did not start. " + await DiagnosticsAsync());
            frontend = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(frontendRecord, token)));
            host = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(hostRecord, token)));
            _ = frontend.SafeHandle;
            _ = host.SafeHandle;
            var records = await File.ReadAllLinesAsync(descendants, token);
            Assert.Contains(ComparisonDescendantSource.IsRunning, records, "The orphaned descendant must be alive before interruption.");
            await auto.WaitUntilTextAsync("batch partial output");

            if (close)
            {
                // This independent client survives the PTY root's final TerminateProcess fallback long enough to witness close.
                var ready = Path.Join(files.DirectoryPath, "observer.ready");
                var observe = new ProcessStartInfo(HostLocator.FindDotnet()) { UseShellExecute = false };
                foreach (var argument in new[]
                {
                    typeof(WindowsConsoleProbe).Assembly.Location,
                    "--windows-console",
                    "observe-close",
                    frontend.Id.ToString(),
                    closeMarker,
                    ready,
                })
                {
                    observe.ArgumentList.Add(argument);
                }

                observer = Process.Start(observe) ?? throw new InvalidOperationException("The console observer did not start.");
                await WaitUntilAsync(() => File.Exists(ready), token);
                await terminal.DisposeAsync();
                await WaitUntilAsync(() => File.Exists(closeMarker) && new FileInfo(closeMarker).Length != 0, token);
                Assert.AreEqual("CTRL_CLOSE_EVENT", await File.ReadAllTextAsync(closeMarker, token));
                await OwnedProcessGroup.WaitForExitAsync(observer, token);
            }
            else
            {
                var signal = new ProcessStartInfo(HostLocator.FindDotnet());
                foreach (var argument in new[]
                {
                    typeof(WindowsConsoleProbe).Assembly.Location,
                    "--windows-console",
                    "break",
                    frontend.Id.ToString(),
                })
                {
                    signal.ArgumentList.Add(argument);
                }

                var sent = await ToolProcess.RunAsync(signal, token);
                Assert.AreEqual(0, sent.ExitCode, sent.StandardError);
                Assert.AreEqual(130, await run.WaitAsync(TimeSpan.FromSeconds(20), token), recorder.Output);
                Assert.Contains("batch partial output", recorder.Output);
                Assert.DoesNotContain("runtime restarted", recorder.Output);
            }

            await OwnedProcessGroup.WaitForExitAsync(frontend, token);
            await OwnedProcessGroup.WaitForExitAsync(host, token);
            await WaitUntilAsync(() => records.All(record => !ComparisonDescendantSource.IsRunning(record)), token);
            Assert.IsTrue(frontend.HasExited);
            Assert.IsTrue(host.HasExited);
            foreach (var record in records)
            {
                Assert.IsFalse(ComparisonDescendantSource.IsRunning(record), record);
            }
        }
        finally
        {
            TestContext.WriteLine(await DiagnosticsAsync());
            if (frontend is { HasExited: false })
            {
                frontend.Kill(entireProcessTree: true);
            }

            if (host is { HasExited: false })
            {
                host.Kill(entireProcessTree: true);
            }

            if (observer is { HasExited: false })
            {
                observer.Kill();
            }

            if (frontend is not null)
            {
                await OwnedProcessGroup.WaitForExitAsync(frontend, CancellationToken.None);
            }

            if (host is not null)
            {
                await OwnedProcessGroup.WaitForExitAsync(host, CancellationToken.None);
            }

            if (observer is not null)
            {
                await OwnedProcessGroup.WaitForExitAsync(observer, CancellationToken.None);
            }

            using var frontendHandle = frontend;
            using var hostHandle = host;
            using var observerHandle = observer;
            if (File.Exists(descendants))
            {
                foreach (var record in await File.ReadAllLinesAsync(descendants, CancellationToken.None))
                {
                    Stop(record);
                }

                static void Stop(string record)
                {
                    using var process = ComparisonDescendantSource.Open(record);
                    if (process is { HasExited: false })
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
            }

            await terminal.DisposeAsync();
            if (close)
            {
                await running.CancelAsync();
            }

            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
            }
            catch (OperationCanceledException) when (close || token.IsCancellationRequested)
            {
                // The close case ends its own run, and a cancelled test ends every run.
            }
            catch (ObjectDisposedException) when (close)
            {
                // The terminal was disposed while its run was still observing the console.
            }
        }

        bool IsReady() => File.Exists(frontendRecord) && File.Exists(descendantsReady)
            && File.ReadAllLines(descendants).Length == 2;

        async Task<string> DiagnosticsAsync()
        {
            var output = new StringBuilder(recorder.Output);
            foreach (var path in s_recordSuffixes.Select(suffix => frontendRecord + suffix))
            {
                try
                {
                    if (File.Exists(path))
                    {
                        output.Append("\n" + Path.GetFileName(path) + ": " + await File.ReadAllTextAsync(path, CancellationToken.None));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    output.Append("\n" + Path.GetFileName(path) + ": " + exception.Message);
                }
            }

            return output.ToString();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            await Task.Delay(10, deadline.Token);
        }
    }
}
