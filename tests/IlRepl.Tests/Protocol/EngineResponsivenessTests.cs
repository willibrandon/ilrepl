using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;
using IlRepl.Tests.Shared;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Exercises independent editing snapshots and cancellation control while real engine work remains active.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class EngineResponsivenessTests
{
    /// <summary>
    /// Supplies cancellation for engine operations and condition-based waits.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Real completion and analysis finish against accepted source while the execution thread is blocked in user code.
    /// </summary>
    [TestMethod]
    public async Task UserExecution_DoesNotBlockCompletionOrAnalysis()
    {
        await using var engine = new InProcessEngine();
        using var permit = new ExecutionPermit();
        var name = ExecutionThreadFixture.Register(permit);
        Task<HandleReply>? running = null;
        try
        {
            await engine.HandleAsync("ldstr \"" + name + "\"", TestContext.CancellationToken);
            var accepted = await engine.HandleAsync("call int32 IlRepl.Tests.Protocol.ExecutionThreadFixture::Wait(string)",
                TestContext.CancellationToken);
            Assert.IsTrue(accepted.Succeeded);
            var revision = engine.Status.Revision;
            running = engine.HandleAsync(".run", TestContext.CancellationToken);
            await permit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
            Assert.IsFalse(running.IsCompleted);
            var completion = await engine.CompleteAsync(new CompletionRequest(["call Console::Wr"], 0, 16, null, []),
                TestContext.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
            var analysis = await engine.AnalyzeAsync(new AnalysisRequest(["pop", "ldc.i4.1", "ret"], 2, 0, 17),
                TestContext.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
            Assert.IsNotEmpty(completion.Items);
            Assert.AreEqual(revision, completion.Revision);
            Assert.AreEqual(revision, analysis.Revision);
            Assert.AreEqual("[int32]", analysis.Stack!.Render());
            Assert.AreEqual(17, analysis.DocumentVersion);
            Assert.IsFalse(running.IsCompleted);
            permit.Release();
            var finished = await running;
            Assert.IsTrue(finished.Succeeded);
            Assert.Contains(line => line.Kind == LineKind.Result && line.PlainText.Contains("42", StringComparison.Ordinal),
                finished.Lines);
        }
        finally
        {
            permit.Release();
            if (running is not null)
            {
                await running;
            }

            ExecutionThreadFixture.Unregister(name);
        }
    }

    /// <summary>
    /// Concurrent and repeated disposal await the actual running cell and release its loaded references exactly once.
    /// </summary>
    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task ConcurrentDisposal_WaitsForExecutingCellAndReferenceCleanup()
    {
        var core = new ReplCore();
        core.Session.Resolver.LoadImage(ModuleInitializerFixture.Create(false));
        await using var engine = new InProcessEngine(core);
        using var permit = new ExecutionPermit();
        var name = ExecutionThreadFixture.Register(permit);
        Task<HandleReply>? running = null;
        try
        {
            Assert.IsTrue((await engine.HandleAsync("ldstr \"" + name + "\"", TestContext.CancellationToken)).Succeeded);
            Assert.IsTrue((await engine.HandleAsync("call int32 IlRepl.Tests.Protocol.ExecutionThreadFixture::Wait(string)",
                TestContext.CancellationToken)).Succeeded);
            running = engine.HandleAsync("ret", TestContext.CancellationToken);
            await permit.Entered.Task.WaitAsync(TestContext.CancellationToken);
            var first = engine.DisposeAsync().AsTask();
            var second = engine.DisposeAsync().AsTask();
            Assert.IsFalse(first.IsCompleted, "Cleanup must wait until actual executing user code returns.");
            Assert.IsFalse(second.IsCompleted, "Every disposal caller must observe the same cleanup boundary.");
            Assert.IsNotEmpty(core.Session.Resolver.LoadedAssemblies);
            permit.Release();
            var reply = await running;
            Assert.IsTrue(reply.Succeeded);
            Assert.Contains(line => line.Kind == LineKind.Result && line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal),
                reply.Lines);
            await Task.WhenAll(first, second).WaitAsync(TestContext.CancellationToken);
            Assert.IsEmpty(core.Session.Resolver.LoadedAssemblies);
            await engine.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.HandleAsync("nop", TestContext.CancellationToken));
        }
        finally
        {
            permit.Release();
            if (running is not null)
            {
                await running;
            }

            ExecutionThreadFixture.Unregister(name);
        }
    }

    /// <summary>
    /// Cooperative host tooling shares cancellation and identity with nested synchronous engine mutations.
    /// </summary>
    [TestMethod]
    public async Task CooperativeOperation_CancelsNestedWorkWithoutReplacingRuntime()
    {
        await using var engine = new InProcessEngine();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new List<ExecutionProgress>();
        engine.ProgressChanged += item =>
        {
            lock (progress)
            {
                progress.Add(item);
            }
        };

        var running = engine.RunOperationAsync("restore", async cancellationToken =>
        {
            var reply = await engine.HandleAsync("nop", cancellationToken);
            Assert.IsTrue(reply.Succeeded);
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        }, TestContext.CancellationToken);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        var active = engine.Progress;
        Assert.IsTrue(active.IsRunning);
        Assert.AreEqual("restore", active.Name);
        Assert.AreEqual(ExecutionPhase.Cooperative, active.Phase);
        Assert.IsFalse(await engine.InterruptAsync("obsolete", TestContext.CancellationToken));
        Assert.IsFalse(running.IsCompleted);
        Assert.IsTrue(await engine.InterruptAsync(active.Identity, TestContext.CancellationToken));
        await Assert.ThrowsAsync<OperationCanceledException>(() => running);
        Assert.IsFalse(engine.Progress.IsRunning);
        Assert.IsTrue(engine.Progress.CancellationRequested);
        Assert.IsFalse(await engine.InterruptAsync(active.Identity, TestContext.CancellationToken));
        lock (progress)
        {
            Assert.HasCount(1, progress.Select(item => item.Identity).Distinct().ToArray());
            Assert.AreSequenceEqual(progress.Select(item => item.Sequence).Order(), progress.Select(item => item.Sequence));
        }

        var stillUsable = await engine.HandleAsync("ldc.i4.s 42", TestContext.CancellationToken);
        Assert.IsTrue(stillUsable.Succeeded);
        Assert.AreEqual("[int32]", stillUsable.Status.Stack);
    }

    /// <summary>
    /// Cancelling before invocation does not mark a loaded dependency as activated, while the later actual call does.
    /// </summary>
    [TestMethod]
    public async Task CancellationBeforeInvocation_DoesNotActivateDependency()
    {
        using var fixture = new SessionDependencyFixture();
        fixture.WritePackage(fixture.AssemblyName, "1.0.0", 42);
        var core = new ReplCore();
        core.Session.Resolver.LoadImage(fixture.PackageImage(fixture.AssemblyName, "1.0.0"));
        await using var engine = new InProcessEngine(core);
        var call = "call int32 [" + fixture.AssemblyName + "]DependencySamples.Values::Read()";
        Assert.IsTrue((await engine.HandleAsync(call, TestContext.CancellationToken)).Succeeded);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        void Cancel(ExecutionProgress progress)
        {
            if (progress.Phase == ExecutionPhase.UserCode && progress.IsRunning)
            {
                cancelled.Cancel();
            }
        }

        engine.ProgressChanged += Cancel;
        await Assert.ThrowsAsync<OperationCanceledException>(() => engine.HandleAsync("ret", cancelled.Token));
        engine.ProgressChanged -= Cancel;
        Assert.IsEmpty(core.Session.ActivatedReferences);
        Assert.AreEqual("[int32]", engine.Status.Stack);
        var completed = await engine.HandleAsync("ret", TestContext.CancellationToken);
        Assert.IsTrue(completed.Succeeded);
        Assert.Contains(line => line.Kind == LineKind.Result && line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal),
            completed.Lines);
        Assert.Contains(fixture.AssemblyName, core.Session.ActivatedReferences);
    }

    /// <summary>
    /// A real user constructor runs only after the source checkpoint and exposes the user-code interruption tier.
    /// </summary>
    [TestMethod]
    public async Task Constructor_EntersUserPhaseAfterAcknowledgedSource()
    {
        var core = new ReplCore();
        await using var engine = new InProcessEngine(core);
        using var permit = new ExecutionPermit();
        var name = ExecutionThreadFixture.Register(permit);
        var checkpoint = false;
        core.BeforeExecution = () => checkpoint = true;
        Task<HandleReply>? running = null;
        try
        {
            Assert.IsTrue((await engine.HandleAsync(".args (string name = \"" + name + "\")",
                TestContext.CancellationToken)).Succeeded);
            Assert.IsTrue((await engine.HandleAsync("ldarg name", TestContext.CancellationToken)).Succeeded);
            var constructor = "newobj instance void IlRepl.Tests.Protocol.ExecutionConstructorFixture::.ctor(string)";
            Assert.IsTrue((await engine.HandleAsync(constructor, TestContext.CancellationToken)).Succeeded);
            running = engine.HandleAsync("ret", TestContext.CancellationToken);
            await permit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
            Assert.IsTrue(checkpoint);
            Assert.AreEqual(ExecutionPhase.UserCode, engine.Progress.Phase);
            Assert.IsTrue(await engine.InterruptAsync(engine.Progress.Identity, TestContext.CancellationToken));
            Assert.IsFalse(running.IsCompleted, "Cooperative cancellation cannot unwind an arbitrary user constructor.");
            permit.Release();
            Assert.IsTrue((await running).Succeeded);
        }
        finally
        {
            permit.Release();
            if (running is not null)
            {
                await running;
            }

            ExecutionThreadFixture.Unregister(name);
        }
    }

    /// <summary>
    /// Cancellation after passive compilation releases it without executing or discarding the accepted cell source.
    /// </summary>
    [TestMethod]
    public async Task CancellationAtInvocationBoundary_RetainsAcceptedSourceAndRuntime()
    {
        var core = new ReplCore();
        await using var engine = new InProcessEngine(core);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        Assert.IsTrue((await engine.HandleAsync("ldc.i4.s 42", TestContext.CancellationToken)).Succeeded);
        var generation = engine.Status.Mark.Generation;
        void Stop(ExecutionProgress progress)
        {
            if (progress.Phase == ExecutionPhase.UserCode)
            {
                cancellation.Cancel();
            }
        }

        engine.ProgressChanged += Stop;
        await Assert.ThrowsAsync<OperationCanceledException>(() => engine.HandleAsync("ret", cancellation.Token));
        engine.ProgressChanged -= Stop;
        Assert.AreEqual(generation, engine.Status.Mark.Generation);
        Assert.AreEqual("[int32]", engine.Status.Stack);
        var snapshot = await engine.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Capture },
        }, TestContext.CancellationToken);

        Assert.IsNotNull(snapshot.Document);
        Assert.Contains(entry => entry.Source.Contains("ldc.i4.s 42"), snapshot.Document.Entries);
        Assert.IsEmpty(snapshot.Document.Cells);
        var result = await engine.HandleAsync("ret", TestContext.CancellationToken);
        Assert.IsTrue(result.Succeeded);
        Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), result.Lines);
    }

    /// <summary>
    /// Deferred module initializers cannot run during cooperative emission before the acknowledged user-code boundary.
    /// </summary>
    [TestMethod]
    public async Task DeferredModuleInitializer_StaysPassiveUntilUserPhase()
    {
        var directory = Directory.CreateTempSubdirectory("ilrepl-phase-").FullName;
        var marker = Path.Combine(directory, "initialized.txt");
        try
        {
            var session = new Session { DeferActivation = true };
            session.Resolver.LoadImage(ModuleInitializerFixture.Create(true, marker));
            var core = new ReplCore(session, new ReplOptions());
            await using var engine = new InProcessEngine(core);
            foreach (var line in new[] { ".method int32 Read() {", "call int32 Owner::Read()", "ret", "}", "call int32 Read()" })
            {
                Assert.IsTrue((await engine.HandleAsync(line, TestContext.CancellationToken)).Succeeded);
            }

            Assert.IsFalse(File.Exists(marker));
            var checkpoint = false;
            var entered = false;
            core.BeforeExecution = () => checkpoint = true;
            engine.ProgressChanged += progress =>
            {
                if (progress.Phase != ExecutionPhase.UserCode)
                {
                    return;
                }

                Assert.IsTrue(checkpoint);
                Assert.IsFalse(File.Exists(marker), "Module initialization must not happen during cooperative compilation.");
                entered = true;
            };

            var reply = await engine.HandleAsync("ret", TestContext.CancellationToken);
            Assert.IsTrue(reply.Succeeded);
            Assert.IsTrue(entered);
            Assert.Contains(line => line.PlainText.Contains("= 142 : int32", StringComparison.Ordinal), reply.Lines);
            Assert.AreEqual("initialized\n", await File.ReadAllTextAsync(marker, TestContext.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
