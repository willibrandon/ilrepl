using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Keeps cancellation tied to the actual remote operation and waits for user execution to settle.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class HostCancellationTests
{
    /// <summary>
    /// Supplies cancellation for the real process and filesystem gates.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Cancelling a live call preserves its terminal observation and cannot cancel a queued or subsequent operation.
    /// </summary>
    [TestMethod]
    [Timeout(45_000, CooperativeCancellation = true)]
    public async Task Cancellation_WaitsForRemoteSettlementAndRejectsOnlyItsQueuedCall()
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var release = Path.Combine(files.DirectoryPath, "release");
        var marker = Path.Combine(files.DirectoryPath, "entered");
        await using var engine = await HostPaths.StartEngineAsync(token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ProgressChanged += progress =>
        {
            if (progress.Phase == ExecutionPhase.UserCode && progress.IsRunning) entered.TrySetResult();
            if (progress.CancellationRequested) cancelling.TrySetResult();
        };
        foreach (var line in new[] { "ldstr " + LiteralParser.Escape(marker), "ldstr \"entered\"",
            "call void File::WriteAllText(string, string)", "WAIT: ldstr " + LiteralParser.Escape(release),
            "call bool File::Exists(string)", "brfalse WAIT", "ldc.i4 73" })
            Assert.IsTrue((await engine.HandleAsync(line, token)).Succeeded);
        using var active = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = engine.HandleAsync("ret", active.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            using var executing = CancellationTokenSource.CreateLinkedTokenSource(token);
            executing.CancelAfter(TimeSpan.FromSeconds(15));
            while (!File.Exists(marker)) await Task.Delay(5, executing.Token);
            using var queued = CancellationTokenSource.CreateLinkedTokenSource(token);
            var waiting = engine.HandleAsync("ldc.i4 99", queued.Token);
            await queued.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
            await active.CancelAsync();
            await cancelling.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            Assert.IsFalse(pending.IsCompleted, "Caller cancellation cannot discard a still-running remote invocation.");
            await File.WriteAllTextAsync(release, "release", token);
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(15), token));
            Assert.IsFalse(engine.Progress.IsRunning);
            Assert.IsTrue((await engine.HandleAsync("ldc.i4 42", token)).Succeeded);
            var next = await engine.HandleAsync("ret", token);
            Assert.IsTrue(next.Succeeded, string.Join('\n', next.Lines.Select(line => line.PlainText)));
            Assert.Contains("= 42 : int32", string.Join('\n', next.Lines.Select(line => line.PlainText)));
        }
        finally
        {
            await File.WriteAllTextAsync(release, "release", CancellationToken.None);
            await engine.TerminateAsync(CancellationToken.None);
        }
    }
}
