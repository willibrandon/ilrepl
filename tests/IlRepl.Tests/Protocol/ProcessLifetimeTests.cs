using System.Diagnostics;
using System.Runtime.InteropServices;
using IlRepl.Engine;
using IlRepl.Processes;
using IlRepl.Protocol;
using IlRepl.Tests.Engine;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Verifies real process ownership, supervisor adoption, and frontend-loss cleanup without replacing the runtime.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed partial class ProcessLifetimeTests
{
    /// <summary>
    /// Supplies cancellation for process and filesystem synchronization.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Supervisor replacement preserves a running invocation, static fields, and the execution process identity.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SupervisorExit_AdoptsBusyRuntimeWithoutLosingState()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var release = Path.Combine(files.DirectoryPath, "release");
        await using var lifetime = new HostProcessLifetime();
        await using var engine = await lifetime.StartAsync(HostPaths.HostAssembly, RepoPaths.Root, cancellationToken: token);
        await SubmitAsync(engine, token, ".class public Keeper {", ".field public static int32 Value", "}",
            "ldc.i4 73", "stsfld int32 Keeper::Value", "ret");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ProgressChanged += progress =>
        {
            if (progress.Phase == ExecutionPhase.UserCode) entered.TrySetResult();
        };
        await SubmitAsync(engine, token, "WAIT: ldstr " + LiteralParser.Escape(release),
            "call bool File::Exists(string)", "brfalse WAIT", "ldsfld int32 Keeper::Value");
        var pending = engine.HandleAsync("ret", token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            var runtime = engine.ProcessId;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var previous = lifetime.Supervision.Epoch;
                var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Observe(ProcessSupervisionState state)
                {
                    if (state.Epoch > previous && !state.Restoring && !state.Degraded) restored.TrySetResult();
                }
                lifetime.SupervisionChanged += Observe;
                try
                {
                    using var supervisor = Process.GetProcessById(lifetime.SupervisorProcessId!.Value);
                    supervisor.Kill();
                    await restored.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
                }
                finally { lifetime.SupervisionChanged -= Observe; }
                Assert.AreEqual(runtime, engine.ProcessId);
                Assert.IsFalse(pending.IsCompleted, "Adoption must not abort or replay the active invocation.");
                Assert.IsFalse(lifetime.Supervision.Degraded);
            }
            await File.WriteAllTextAsync(release, "release", token);
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(15), token);
            Assert.IsTrue(result.Succeeded, Text(result));
            Assert.Contains("= 73 : int32", Text(result));
            await SubmitAsync(engine, token, "ldsfld int32 Keeper::Value");
            Assert.Contains("= 73 : int32", Text(await engine.HandleAsync("ret", token)));
        }
        finally
        {
            await File.WriteAllTextAsync(release, "release", CancellationToken.None);
            await engine.TerminateAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Replacing a runtime reuses the frontend's supervisor and starts a different host with fresh state.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task RuntimeReplacement_ReusesSupervisor()
    {
        var token = TestContext.CancellationToken;
        await using var lifetime = new HostProcessLifetime();
        var first = await lifetime.StartAsync(HostPaths.HostAssembly, cancellationToken: token);
        var supervisor = lifetime.SupervisorProcessId;
        var host = first.ProcessId;
        await first.DisposeAsync();
        await using var second = await lifetime.StartAsync(HostPaths.HostAssembly, cancellationToken: token);
        Assert.AreEqual(supervisor, lifetime.SupervisorProcessId);
        Assert.AreNotEqual(host, second.ProcessId);
        await SubmitAsync(second, token, "ldc.i4 42");
        Assert.Contains("= 42 : int32", Text(await second.HandleAsync("ret", token)));
    }

    /// <summary>
    /// A missing supervisor package degrades ownership without killing the runtime, and an explicit retry adopts its state.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task FailedAdoption_PreservesRuntimeAndExplicitRetryRestoresOwnership()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            File.CreateSymbolicLink(Path.Combine(files.DirectoryPath, Path.GetFileName(file)), file);
        var assembly = Path.Combine(files.DirectoryPath, "ilrepl.dll");
        await using var lifetime = new HostProcessLifetime(assembly);
        await using var engine = await lifetime.StartAsync(HostPaths.HostAssembly, cancellationToken: token);
        await SubmitAsync(engine, token, ".class public Keeper {", ".field public static int32 Value", "}",
            "ldc.i4 73", "stsfld int32 Keeper::Value", "ret");
        var runtime = engine.ProcessId;
        var degraded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.SupervisionChanged += state => { if (state.Degraded) degraded.TrySetResult(); };
        File.Move(assembly, assembly + ".unavailable");
        using (var supervisor = Process.GetProcessById(lifetime.SupervisorProcessId!.Value)) supervisor.Kill();
        await degraded.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        Assert.AreEqual(runtime, engine.ProcessId);
        Assert.IsTrue(lifetime.Supervision.Degraded);
        await Assert.ThrowsAsync<ReplEngineException>(() => engine.HandleAsync("ldc.i4.1", token));
        File.Move(assembly + ".unavailable", assembly);
        await engine.RetrySupervisionAsync(token);
        Assert.IsFalse(lifetime.Supervision.Degraded);
        Assert.AreEqual(runtime, engine.ProcessId);
        await SubmitAsync(engine, token, "ldsfld int32 Keeper::Value");
        Assert.Contains("= 73 : int32", Text(await engine.HandleAsync("ret", token)));
    }

    /// <summary>
    /// Killing only the frontend stops its hung host and grandchildren whose intermediate parent has already exited.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public Task FrontendKilled_StopsHostAndOrphanedDescendants() => VerifyFrontendLossAsync(false);

    /// <summary>
    /// Losing the frontend while it cannot replace a dead supervisor still stops hung execution and live descendants.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public Task FrontendKilledDuringAdoption_StopsHostAndDescendants() => VerifyFrontendLossAsync(true);

    private async Task VerifyFrontendLossAsync(bool adoptionGap)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var hostRecord = Path.Combine(files.DirectoryPath, "host");
        var descendants = Path.Combine(files.DirectoryPath, "processes");
        var start = new ProcessStartInfo(HostLocator.FindDotnet())
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(RepoPaths.FrontEndAssembly);
        start.ArgumentList.Add("--batch");
        start.ArgumentList.Add("--quiet");
        start.ArgumentList.Add("--no-color");
        using var frontend = Process.Start(start) ?? throw new IOException("the frontend did not start");
        var stdout = frontend.StandardOutput.ReadToEndAsync(token);
        var stderr = frontend.StandardError.ReadToEndAsync(token);
        Process? host = null;
        try
        {
            string[] source =
            [
                ".load " + LiteralParser.Escape(typeof(ComparisonDescendantSource).Assembly.Location),
                "ldstr " + LiteralParser.Escape(hostRecord), "call int32 Environment::get_ProcessId()", "box int32",
                "callvirt instance string Object::ToString()", "call void File::WriteAllText(string, string)",
                "ldstr " + LiteralParser.Escape(Environment.ProcessPath!), "ldstr " + LiteralParser.Escape(files.DirectoryPath),
                "ldc.i4.1", "ldstr \"timeout\"", "ldc.i4.0",
                "call int32 [IlRepl.Tests]IlRepl.Tests.Engine.ComparisonDescendantSource::Run(string, string, bool, string, bool)",
                "ret",
            ];
            foreach (var line in source) await frontend.StandardInput.WriteLineAsync(line.AsMemory(), token);
            await frontend.StandardInput.FlushAsync(token);
            await WaitUntilAsync(() => File.Exists(hostRecord) && File.Exists(descendants)
                && File.ReadAllLines(descendants).Length == 2, token);
            host = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(hostRecord, token)));
            var hostIdentity = host.Id + " " + host.StartTime.ToUniversalTime().Ticks;
            if (adoptionGap)
            {
                var supervisorId = await ParentProcessIdAsync(host.Id, token);
                using var supervisor = Process.GetProcessById(supervisorId);
                var identity = supervisor.Id + " " + supervisor.StartTime.ToUniversalTime().Ticks;
                Assert.AreEqual(0, Signal(frontend.Id, OperatingSystem.IsMacOS() ? 17 : 19));
                supervisor.Kill();
                await WaitUntilAsync(() => !IsExecuting(identity), token);
            }
            frontend.Kill();
            await frontend.WaitForExitAsync(token);
            await WaitUntilAsync(() => !IsExecuting(hostIdentity), token);
            await WaitUntilAsync(() => File.ReadAllLines(descendants).All(record => !IsExecuting(record)), token);
            Assert.IsFalse(IsExecuting(hostIdentity));
            foreach (var descendant in await File.ReadAllLinesAsync(descendants, token))
                Assert.IsFalse(IsExecuting(descendant), descendant);
        }
        finally
        {
            if (!frontend.HasExited) frontend.Kill(entireProcessTree: true);
            if (host is not null)
            {
                if (!host.HasExited) host.Kill(entireProcessTree: true);
                host.Dispose();
            }
            if (File.Exists(descendants))
                foreach (var record in await File.ReadAllLinesAsync(descendants, CancellationToken.None))
                {
                    using var process = ComparisonDescendantSource.Open(record);
                    if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
                }
            await Task.WhenAll(stdout, stderr);
        }
    }

    private static async Task<int> ParentProcessIdAsync(int process, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            var status = await File.ReadAllTextAsync("/proc/" + process + "/stat", cancellationToken);
            return int.Parse(status[(status.LastIndexOf(')') + 2)..].Split(' ')[1]);
        }
        var start = new ProcessStartInfo("/bin/ps");
        foreach (var argument in new[] { "-o", "ppid=", "-p", process.ToString() }) start.ArgumentList.Add(argument);
        var result = await ToolProcess.RunAsync(start, cancellationToken);
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        return int.Parse(result.StandardOutput.Trim());
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Signal(int process, int signal);

    private static bool IsExecuting(string identity)
    {
        using var process = ComparisonDescendantSource.Open(identity);
        if (process is null || process.HasExited) return false;
        if (!OperatingSystem.IsLinux()) return true;
        var path = "/proc/" + process.Id + "/stat";
        if (!File.Exists(path)) return false;
        var status = File.ReadAllText(path);
        return status[status.LastIndexOf(')') + 2] is not ('Z' or 'X');
    }

    private static async Task SubmitAsync(HostProcessEngine engine, CancellationToken cancellationToken, params string[] lines)
    {
        foreach (var line in lines)
        {
            var reply = await engine.HandleAsync(line, cancellationToken);
            Assert.IsTrue(reply.Succeeded, line + "\n" + Text(reply));
        }
    }

    private static string Text(HandleReply reply) => string.Join('\n', reply.Lines.Select(line => line.PlainText));

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
