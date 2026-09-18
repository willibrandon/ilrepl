using System.Collections.Concurrent;
using IlRepl.Engine;
using IlRepl.Protocol;
using IlRepl.Repl;
using StreamJsonRpc;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Verifies completion carried by actual source replies without weakening acknowledged execution or mutation ordering.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class HostCompletionTests
{
    /// <summary>
    /// Supplies cancellation for real engine, transport, and child-host operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Both source entry points retain their source checkpoint and carry completion without a separate terminal callback.
    /// </summary>
    /// <param name="located">Whether the source carries an explicit editor location.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task SourceReply_CarriesCompletionAfterAcknowledgedSource(bool located)
    {
        var token = TestContext.CancellationToken;
        await using var connection = new HostProgressConnection(new ReplCore());
        var reply = located
            ? await connection.Proxy.HandleSourceAsync("ldc.i4.s 42", new AnalysisLocation("source", 7, 0, 11), token)
            : await connection.Proxy.HandleAsync("ldc.i4.s 42", token);

        Assert.IsTrue(reply.Succeeded);
        Assert.AreEqual("[int32]", reply.Status.Stack);
        var started = Assert.ContainsSingle(connection.Progress);
        Assert.IsTrue(started.IsRunning);
        Assert.AreEqual(ExecutionPhase.Cooperative, started.Phase);
        Assert.IsNotNull(reply.CompletionProgress);
        Assert.AreEqual(started.Identity, reply.CompletionProgress.Identity);
        Assert.IsFalse(reply.CompletionProgress.IsRunning);
        Assert.AreEqual(ExecutionPhase.Cleanup, reply.CompletionProgress.Phase);
        Assert.IsGreaterThan(started.Sequence, reply.CompletionProgress.Sequence);
        var checkpoint = Assert.ContainsSingle(connection.Checkpoints);
        Assert.AreSequenceEqual<string>(["ldc.i4.s 42"], checkpoint.Document.Entries.SelectMany(entry => entry.Source));
        Assert.AreEqual(reply.Status, checkpoint.Reply.Status);
        var result = await connection.Proxy.HandleAsync("ret", token);
        Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), result.Lines);
    }

    /// <summary>
    /// A real engine exception waits for its independent terminal acknowledgement before the exceptional RPC reply arrives.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ExceptionReply_AcknowledgesCompletionBeforePropagating()
    {
        var token = TestContext.CancellationToken;
        var core = new ReplCore();
        await using var connection = new HostProgressConnection(core);
        var checkpoint = core.SourceCheckpoint;
        core.SourceCheckpoint = () => throw new InvalidOperationException("source checkpoint fault");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.AcknowledgeProgress = progress =>
        {
            if (progress.IsRunning) return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        var pending = connection.Proxy.HandleAsync("ldc.i4.s 42", token);
        try
        {
            await entered.Task.WaitAsync(token);
            Assert.IsFalse(pending.IsCompleted);
            Assert.HasCount(2, connection.Progress);
            Assert.IsFalse(connection.Progress.Last().IsRunning);
            Assert.AreEqual(connection.Progress.First().Identity, connection.Progress.Last().Identity);
            release.TrySetResult();
            var exception = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(() => pending);
            Assert.Contains("source checkpoint fault", exception.Message);
            core.SourceCheckpoint = checkpoint;
            var next = await connection.Proxy.HandleAsync("ret", token);
            Assert.IsTrue(next.Succeeded);
            Assert.IsNotNull(next.CompletionProgress);
            Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), next.Lines);
        }
        finally
        {
            release.TrySetResult();
            core.SourceCheckpoint = checkpoint;
            try { await pending; }
            catch (RemoteInvocationException) { }
        }
    }

    /// <summary>
    /// Cooperative cancellation acknowledges final progress before its cancelled RPC reply and leaves later source executable.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task CancellationReply_AcknowledgesCompletionBeforePropagating()
    {
        var token = TestContext.CancellationToken;
        await using var connection = new HostProgressConnection(new ReplCore());
        var started = new TaskCompletionSource<ExecutionProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<ExecutionProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.AcknowledgeProgress = progress =>
        {
            if (!progress.IsRunning)
            {
                finished.TrySetResult(progress);
                return releaseFinish.Task;
            }
            if (!progress.CancellationRequested)
            {
                started.TrySetResult(progress);
                return releaseStart.Task;
            }
            return Task.CompletedTask;
        };
        var pending = connection.Proxy.HandleAsync("ldc.i4.s 99", CancellationToken.None);
        try
        {
            var active = await started.Task.WaitAsync(token);
            Assert.IsTrue(await connection.Proxy.InterruptAsync(active.Identity, token));
            Assert.IsFalse(pending.IsCompleted);
            Assert.IsEmpty(connection.Checkpoints);
            releaseStart.TrySetResult();
            var completion = await finished.Task.WaitAsync(token);
            Assert.AreEqual(active.Identity, completion.Identity);
            Assert.IsTrue(completion.CancellationRequested);
            Assert.IsFalse(completion.IsRunning);
            Assert.IsFalse(pending.IsCompleted, "The exceptional reply must wait for terminal notification acknowledgement.");
            releaseFinish.TrySetResult();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(token));
            Assert.IsFalse(await connection.Proxy.InterruptAsync(active.Identity, token));
            Assert.IsEmpty(connection.Checkpoints, "Cancellation before the start acknowledgement must not accept source.");
            Assert.IsTrue((await connection.Proxy.HandleAsync("ldc.i4.s 42", token)).Succeeded);
            var result = await connection.Proxy.HandleAsync("ret", token);
            Assert.IsTrue(result.Succeeded);
            Assert.IsNotNull(result.CompletionProgress);
            Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), result.Lines);
        }
        finally
        {
            releaseStart.TrySetResult();
            releaseFinish.TrySetResult();
            try { await pending; }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// A load acknowledges the parsing operation before its dependency operation begins and carries only the final completion.
    /// </summary>
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Load_FlushesPrecedingCompletionBeforeStartingDependencyWork()
    {
        var token = TestContext.CancellationToken;
        // The real host runs in this process, so its mapped assembly needs a build-owned lifetime on Windows.
        var path = SampleHost.Samples.GreeterDll;
        await using var connection = new HostProgressConnection(new ReplCore());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.AcknowledgeProgress = progress =>
        {
            if (progress.Name != "submission" || progress.IsRunning) return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        var pending = connection.Proxy.HandleAsync(".load " + LiteralParser.Escape(path), token);
        try
        {
            await entered.Task.WaitAsync(token);
            Assert.IsFalse(pending.IsCompleted);
            Assert.HasCount(2, connection.Progress);
            Assert.AreEqual(connection.Progress.First().Identity, connection.Progress.Last().Identity);
            Assert.DoesNotContain(progress => progress.Name == "load", connection.Progress);
            release.TrySetResult();
            var reply = await pending;
            Assert.IsTrue(reply.Succeeded, string.Join('\n', reply.Lines.Select(line => line.PlainText)));
            Assert.IsNotNull(reply.CompletionProgress);
            Assert.AreEqual("load", reply.CompletionProgress.Name);
            Assert.IsFalse(reply.CompletionProgress.IsRunning);
            Assert.HasCount(3, connection.Progress);
            Assert.AreEqual("load", connection.Progress.Last().Name);
            Assert.IsTrue(connection.Progress.Last().IsRunning);
            Assert.AreEqual(connection.Progress.Last().Identity, reply.CompletionProgress.Identity);
            Assert.AreNotEqual(connection.Progress.First().Identity, reply.CompletionProgress.Identity);
            Assert.AreSequenceEqual(connection.Progress.Select(progress => progress.Sequence).Order(),
                connection.Progress.Select(progress => progress.Sequence));
            foreach (var line in new[] { "ldc.i4.s 20", "ldc.i4.s 22", "call int32 [Greeter]Greeter.Hello::Add(int32, int32)" })
                Assert.IsTrue((await connection.Proxy.HandleAsync(line, token)).Succeeded);
            var result = await connection.Proxy.HandleAsync("ret", token);
            Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), result.Lines);
        }
        finally
        {
            release.TrySetResult();
            await pending;
        }
    }

    /// <summary>
    /// Observing carried completion retains the frontend mutation gate and cancelled queued work cannot reach the host.
    /// </summary>
    /// <param name="located">Whether the source carries an explicit editor location.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(45_000, CooperativeCancellation = true)]
    public async Task CompletionObserver_HoldsReplyAndMutationGate(bool located)
    {
        var token = TestContext.CancellationToken;
        await using var host = await HostPaths.StartEngineAsync(token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var observed = new ConcurrentQueue<ExecutionProgress>();
        var held = 0;
        host.ProgressChanged += progress =>
        {
            observed.Enqueue(progress);
            if (!progress.IsRunning && Interlocked.Exchange(ref held, 1) == 0)
            {
                entered.TrySetResult();
                release.Wait(token);
            }
        };
        var first = located
            ? host.HandleSourceAsync("ldc.i4.6", new AnalysisLocation("source", 0, 0, 8), token)
            : host.HandleAsync("ldc.i4.6", token);
        Task<HandleReply>? second = null;
        try
        {
            await entered.Task.WaitAsync(token);
            Assert.IsFalse(first.IsCompleted);
            Assert.IsFalse(host.Progress.IsRunning);
            second = host.HandleAsync("ldc.i4.7", token);
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
            var queued = host.HandleAsync("ldc.i4.s 99", cancelled.Token);
            await cancelled.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
            Assert.IsFalse(second.IsCompleted);
            Assert.HasCount(2, observed);
            Assert.HasCount(1, observed.Select(progress => progress.Identity).Distinct());
            release.Set();
            var firstReply = await first;
            Assert.IsTrue(firstReply.Succeeded);
            Assert.IsNotNull(firstReply.CompletionProgress);
            Assert.AreEqual(observed.ElementAt(1), firstReply.CompletionProgress);
            Assert.IsTrue((await second).Succeeded);
            Assert.IsFalse(host.Progress.IsRunning);
            Assert.IsTrue((await host.HandleAsync("mul", token)).Succeeded);
            var result = await host.HandleAsync("ret", token);
            Assert.IsTrue(result.Succeeded);
            Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), result.Lines);
        }
        finally
        {
            release.Set();
            await first;
            if (second is not null) await second;
        }
    }
}
