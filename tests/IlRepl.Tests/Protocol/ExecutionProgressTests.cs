using System.Collections.Concurrent;
using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Verifies asynchronous progress delivery without relaxing operation ordering or acknowledged user-execution boundaries.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class ExecutionProgressTests
{
    /// <summary>
    /// Supplies cancellation for actual engine work, host execution, and acknowledgement gates.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Pending start delivery yields the caller while keeping a real nested submission behind its acknowledgement.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task StartAcknowledgement_YieldsCallerAndDelaysRealSubmission()
    {
        var token = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new ConcurrentQueue<ExecutionProgress>();
        engine.ProgressChanged += observed.Enqueue;
        engine.ProgressPublisher = progress =>
        {
            if (!progress.IsRunning)
            {
                return Task.CompletedTask;
            }

            entered.TrySetResult();
            return release.Task;
        };
        Task<HandleReply>? pending = null;
        var caller = Task.Run(() =>
        {
            pending = engine.RunOperationAsync("accepted source", cancellation => engine.HandleAsync("ldc.i4.s 42", cancellation), token);
        }, token);
        try
        {
            await entered.Task.WaitAsync(token);
            await caller.WaitAsync(token);
            Assert.IsNotNull(pending);
            Assert.IsFalse(pending.IsCompleted);
            Assert.IsTrue(engine.Status.CellIsEmpty, "Source cannot enter the engine before start delivery is acknowledged.");
            var started = Assert.ContainsSingle(observed);
            Assert.IsTrue(started.IsRunning);
            Assert.AreEqual(ExecutionPhase.Cooperative, started.Phase);
            Assert.AreEqual(started, engine.Progress);

            release.TrySetResult();
            var reply = await pending;

            Assert.IsTrue(reply.Succeeded);
            Assert.AreEqual("[int32]", reply.Status.Stack);
            Assert.HasCount(2, observed);
            Assert.AreEqual(started.Identity, observed.Last().Identity);
            Assert.IsFalse(observed.Last().IsRunning);
            Assert.AreEqual(ExecutionPhase.Cleanup, observed.Last().Phase);
            Assert.IsGreaterThan(started.Sequence, observed.Last().Sequence);
        }
        finally
        {
            release.TrySetResult();
            await caller;
            if (pending is not null)
            {
                await pending;
            }
        }
    }

    /// <summary>
    /// Pending terminal delivery retains both the operation reply and serialization gate until the frontend acknowledges it.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task CompletionAcknowledgement_HoldsOperationGateAndReply()
    {
        var token = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ProgressChanged += progress =>
        {
            if (progress.Name == "second" && progress.IsRunning)
            {
                secondStarted.TrySetResult();
            }
        };
        engine.ProgressPublisher = progress =>
        {
            if (progress.Name != "first" || progress.IsRunning)
            {
                return Task.CompletedTask;
            }

            entered.TrySetResult();
            return release.Task;
        };
        var first = engine.RunOperationAsync("first", cancellation => engine.HandleAsync("ldc.i4.6", cancellation), token);
        Task<HandleReply>? second = null;
        try
        {
            await entered.Task.WaitAsync(token);
            Assert.IsFalse(first.IsCompleted);
            second = engine.RunOperationAsync("second", cancellation => engine.HandleAsync("ldc.i4.7", cancellation), token);
            Assert.IsFalse(secondStarted.Task.IsCompleted, "The next operation must wait for the preceding terminal acknowledgement.");
            Assert.IsFalse(second.IsCompleted);
            Assert.AreEqual("[int32]", engine.Status.Stack);

            release.TrySetResult();
            Assert.IsTrue((await first).Succeeded);
            Assert.IsTrue((await second).Succeeded);
            Assert.IsTrue(secondStarted.Task.IsCompleted);
            Assert.IsTrue((await engine.HandleAsync("mul", token)).Succeeded);
            var reply = await engine.HandleAsync("ret", token);

            Assert.IsTrue(reply.Succeeded);
            Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), reply.Lines);
        }
        finally
        {
            release.TrySetResult();
            await first;
            if (second is not null)
            {
                await second;
            }
        }
    }

    /// <summary>
    /// Interruption delivery yields its caller and retains the cancelled operation's terminal state before another cell runs.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task InterruptAcknowledgement_YieldsCallerAndPreservesFinalCancellation()
    {
        var token = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interruptEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInterrupt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new ConcurrentQueue<ExecutionProgress>();
        engine.ProgressChanged += observed.Enqueue;
        engine.ProgressPublisher = progress =>
        {
            if (!progress.IsRunning || !progress.CancellationRequested)
            {
                return Task.CompletedTask;
            }

            interruptEntered.TrySetResult();
            return releaseInterrupt.Task;
        };
        var pending = engine.RunOperationAsync("cancelled work", async cancellation =>
        {
            Assert.IsTrue((await engine.HandleAsync("ldc.i4.s 42", cancellation)).Succeeded);
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }
            finally
            {
                cleanupEntered.TrySetResult();
                await releaseCleanup.Task;
            }

            return true;
        }, token);
        Task<bool>? interrupt = null;
        Task? caller = null;
        try
        {
            await entered.Task.WaitAsync(token);
            var identity = engine.Progress.Identity;
            caller = Task.Run(() =>
            {
                interrupt = engine.InterruptAsync(identity, token);
            }, token);
            await interruptEntered.Task.WaitAsync(token);
            await caller.WaitAsync(token);
            await cleanupEntered.Task.WaitAsync(token);
            Assert.IsNotNull(interrupt);
            Assert.IsFalse(interrupt.IsCompleted);
            Assert.IsFalse(pending.IsCompleted);
            Assert.IsTrue(engine.Progress.IsRunning);
            Assert.IsTrue(engine.Progress.CancellationRequested);

            releaseInterrupt.TrySetResult();
            Assert.IsTrue(await interrupt);
            Assert.IsFalse(pending.IsCompleted, "Acknowledging interruption must not bypass real operation cleanup.");
            releaseCleanup.TrySetResult();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);

            Assert.IsFalse(engine.Progress.IsRunning);
            Assert.IsTrue(engine.Progress.CancellationRequested);
            Assert.AreEqual(engine.Progress, observed.Last());
            Assert.HasCount(1, observed.Select(progress => progress.Identity).Distinct());
            Assert.AreSequenceEqual(observed.Select(progress => progress.Sequence).Order(), observed.Select(progress => progress.Sequence));
            Assert.IsFalse(await engine.InterruptAsync(identity, token));
            var reply = await engine.HandleAsync("ret", token);
            Assert.IsTrue(reply.Succeeded);
            Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), reply.Lines);
        }
        finally
        {
            releaseInterrupt.TrySetResult();
            releaseCleanup.TrySetResult();
            if (caller is not null)
            {
                await caller;
            }

            if (interrupt is not null)
            {
                await interrupt;
            }

            if (!pending.IsCompleted)
            {
                await engine.InterruptAsync(engine.Progress.Identity, CancellationToken.None);
            }

            try
            {
                await pending;
            }
            catch (OperationCanceledException) when (pending.IsCanceled)
            {
            }
        }
    }

    /// <summary>
    /// Real host execution waits for source and user-phase acknowledgements and retains its reply until terminal delivery completes.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Host_UserInvocationWaitsForCheckpointAndPhaseAcknowledgements()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        await using var host = await HostPaths.StartEngineAsync(token);
        string[] source = ["ldstr " + LiteralParser.Escape(files.MarkerPath), "ldstr \"executed\"",
            "call void File::WriteAllText(string, string)", "ldc.i4.s 42"];
        foreach (var line in source)
        {
            Assert.IsTrue((await host.HandleAsync(line, token)).Succeeded);
        }

        var checkpointEntered = new TaskCompletionSource<SessionReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCheckpoint = new ManualResetEventSlim();
        using var releaseInvocation = new ManualResetEventSlim();
        using var releaseFinished = new ManualResetEventSlim();
        var observed = new ConcurrentQueue<ExecutionProgress>();
        host.CheckpointReceived += checkpoint =>
        {
            if (checkpoint.PendingSubmission is null)
            {
                return;
            }

            checkpointEntered.TrySetResult(checkpoint);
            releaseCheckpoint.Wait(token);
        };
        host.ProgressChanged += progress =>
        {
            observed.Enqueue(progress);
            if (progress.IsRunning && progress.Phase == ExecutionPhase.UserCode)
            {
                invocationEntered.TrySetResult();
                releaseInvocation.Wait(token);
            }
            else if (!progress.IsRunning)
            {
                finishedEntered.TrySetResult();
                releaseFinished.Wait(token);
            }
        };
        var pending = host.HandleAsync("ret", token);
        try
        {
            var checkpoint = await checkpointEntered.Task.WaitAsync(token);
            Assert.AreSequenceEqual<string>([.. source, "ret"], checkpoint.PendingSource);
            Assert.IsFalse(File.Exists(files.MarkerPath));
            Assert.IsFalse(invocationEntered.Task.IsCompleted);
            Assert.IsFalse(pending.IsCompleted);

            releaseCheckpoint.Set();
            await invocationEntered.Task.WaitAsync(token);
            Assert.IsFalse(File.Exists(files.MarkerPath), "User IL must wait until its phase notification is acknowledged.");
            Assert.IsFalse(pending.IsCompleted);

            releaseInvocation.Set();
            await finishedEntered.Task.WaitAsync(token);
            Assert.AreEqual("executed", await File.ReadAllTextAsync(files.MarkerPath, token));
            Assert.IsFalse(pending.IsCompleted, "The reply cannot precede acknowledgement of its final progress state.");
            Assert.IsFalse(host.Progress.IsRunning);
            Assert.AreEqual(ExecutionPhase.Cleanup, host.Progress.Phase);

            releaseFinished.Set();
            var reply = await pending;
            Assert.IsTrue(reply.Succeeded);
            Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), reply.Lines);
            Assert.HasCount(1, observed.Select(progress => progress.Identity).Distinct());
            Assert.AreEqual(ExecutionPhase.Cooperative, observed.First().Phase);
            Assert.IsTrue(observed.First().IsRunning);
            Assert.Contains(progress => progress.Phase == ExecutionPhase.UserCode && progress.IsRunning, observed);
            Assert.AreEqual(host.Progress, observed.Last());
            Assert.AreSequenceEqual(observed.Select(progress => progress.Sequence).Order(), observed.Select(progress => progress.Sequence));
        }
        finally
        {
            releaseCheckpoint.Set();
            releaseInvocation.Set();
            releaseFinished.Set();
            await pending;
        }
    }
}
