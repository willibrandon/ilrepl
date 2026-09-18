using System.Diagnostics;
using Hex1b;
using Hex1b.Automation;
using IlRepl.Engine;
using IlRepl.Processes;
using IlRepl.Tests.Engine;

namespace IlRepl.Tests.EndToEnd;

/// <summary>
/// Exercises native Windows console interruption and closure against real frontends, hosts, and orphaned descendants.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class WindowsConsoleTests
{
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
        var frontendRecord = Path.Combine(files.DirectoryPath, "frontend.pid");
        var hostRecord = Path.Combine(files.DirectoryPath, "host.pid");
        var closeMarker = Path.Combine(files.DirectoryPath, "console.closed");
        var descendants = Path.Combine(files.DirectoryPath, "processes");
        var recorder = new WorkloadRecorder();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithPtyProcess(options =>
        {
            options.FileName = HostLocator.FindDotnet();
            // The direct backend closes ConPTY itself, without a proxy process-tree kill racing the close event.
            options.WindowsPtyMode = WindowsPtyMode.Direct;
            options.Arguments = [typeof(WindowsConsoleProbe).Assembly.Location, "--windows-console", "launch",
                frontendRecord, RepoPaths.FrontEndAssembly, "--batch", "--no-color"];
            options.WorkingDirectory = files.DirectoryPath;
        }).AddWorkloadFilter(recorder).WithHeadless().WithDimensions(100, 30).Build();
        var run = terminal.RunAsync(token);
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(20));
        Process? frontend = null;
        Process? host = null;
        Process? observer = null;
        try
        {
            string[] source =
            [
                ".load " + LiteralParser.Escape(typeof(ComparisonDescendantSource).Assembly.Location),
                "ldstr " + LiteralParser.Escape(hostRecord), "call int32 Environment::get_ProcessId()", "box int32",
                "callvirt instance string Object::ToString()", "call void File::WriteAllText(string, string)",
                "ldstr \"batch partial output\"", "call void Console::WriteLine(string)",
                "ldstr " + LiteralParser.Escape(Environment.ProcessPath!), "ldstr " + LiteralParser.Escape(files.DirectoryPath),
                "ldc.i4.1", "ldstr \"timeout\"", "ldc.i4.1",
                "call int32 [IlRepl.Tests]IlRepl.Tests.Engine.ComparisonDescendantSource::Run(string, string, bool, string, bool)",
                "ret",
            ];
            foreach (var line in source)
            {
                await auto.TypeAsync(line, ct: token);
                await auto.EnterAsync(ct: token);
            }
            await auto.WaitUntilAsync(_ => File.Exists(frontendRecord) && File.Exists(hostRecord)
                && File.Exists(descendants) && File.ReadAllLines(descendants).Length == 2);
            frontend = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(frontendRecord, token)));
            host = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(hostRecord, token)));
            var records = await File.ReadAllLinesAsync(descendants, token);
            Assert.Contains(ComparisonDescendantSource.IsRunning, records, "The orphaned descendant must be alive before interruption.");
            await auto.WaitUntilTextAsync("batch partial output");

            if (close)
            {
                // This independent client survives the PTY root's final TerminateProcess fallback long enough to witness close.
                var ready = Path.Combine(files.DirectoryPath, "observer.ready");
                var observe = new ProcessStartInfo(HostLocator.FindDotnet()) { UseShellExecute = false };
                foreach (var argument in new[] { typeof(WindowsConsoleProbe).Assembly.Location, "--windows-console",
                    "observe-close", frontend.Id.ToString(), closeMarker, ready }) observe.ArgumentList.Add(argument);
                observer = Process.Start(observe) ?? throw new InvalidOperationException("The console observer did not start.");
                await WaitUntilAsync(() => File.Exists(ready), token);
                await terminal.DisposeAsync();
                await WaitUntilAsync(() => File.Exists(closeMarker) && new FileInfo(closeMarker).Length != 0, token);
                Assert.AreEqual("CTRL_CLOSE_EVENT", await File.ReadAllTextAsync(closeMarker, token));
                await observer.WaitForExitAsync(token);
            }
            else
            {
                var signal = new ProcessStartInfo(HostLocator.FindDotnet());
                foreach (var argument in new[] { typeof(WindowsConsoleProbe).Assembly.Location, "--windows-console",
                    "break", frontend.Id.ToString() }) signal.ArgumentList.Add(argument);
                var sent = await ToolProcess.RunAsync(signal, token);
                Assert.AreEqual(0, sent.ExitCode, sent.StandardError);
                Assert.AreEqual(130, await run.WaitAsync(TimeSpan.FromSeconds(20), token), recorder.Output);
                Assert.Contains("batch partial output", recorder.Output);
                Assert.DoesNotContain("runtime restarted", recorder.Output);
            }
            await frontend.WaitForExitAsync(token);
            await host.WaitForExitAsync(token);
            await WaitUntilAsync(() => records.All(record => !ComparisonDescendantSource.IsRunning(record)), token);
            Assert.IsTrue(frontend.HasExited);
            Assert.IsTrue(host.HasExited);
            foreach (var record in records) Assert.IsFalse(ComparisonDescendantSource.IsRunning(record), record);
        }
        finally
        {
            if (frontend is { HasExited: false }) frontend.Kill(entireProcessTree: true);
            if (host is { HasExited: false }) host.Kill(entireProcessTree: true);
            if (observer is { HasExited: false }) observer.Kill();
            frontend?.Dispose();
            host?.Dispose();
            observer?.Dispose();
            if (File.Exists(descendants))
                foreach (var record in await File.ReadAllLinesAsync(descendants, CancellationToken.None))
                {
                    using var process = ComparisonDescendantSource.Open(record);
                    if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
                }
            await terminal.DisposeAsync();
            try { await run.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None); }
            catch (OperationCanceledException) when (close || token.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (close) { }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
}
