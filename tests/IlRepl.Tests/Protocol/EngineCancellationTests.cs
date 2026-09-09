using IlRepl.Repl;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Queued engine mutations honor cancellation before acquiring the execution gate.
/// </summary>
[TestClass]
public sealed class EngineCancellationTests
{
    /// <summary>
    /// Supplies cancellation for test operations.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Cancelling queued input or rollback settles immediately and leaves the session unchanged.
    /// </summary>
    /// <param name="rollback">True to queue rollback instead of an instruction.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task CancelledGateWait_DoesNotRunTheOperation(bool rollback)
    {
        var ct = TestContext.CancellationToken;
        await using var engine = new InProcessEngine();
        var mark = engine.Status.Mark;
        var prefix = Path.Combine(Path.GetTempPath(), "ilrepl-cancel-gate-" + Guid.NewGuid().ToString("N"));
        var started = prefix + ".started";
        var release = prefix + ".release";
        var startedLiteral = started.Replace("\\", "\\\\", StringComparison.Ordinal);
        var releaseLiteral = release.Replace("\\", "\\\\", StringComparison.Ordinal);
        foreach (var line in new[]
        {
            $"ldstr \"{startedLiteral}\"", "ldstr \"started\"", "call File::WriteAllText(string, string)",
            $"WAIT: ldstr \"{releaseLiteral}\"", "call File::Exists(string)", "brtrue DONE", "ldc.i4.5",
            "call Thread::Sleep(int32)", "br WAIT", "DONE: ldc.i4.s 42",
        })
        {
            Assert.IsTrue((await engine.HandleAsync(line, ct)).Succeeded, line);
        }

        var running = Task.Run(() => engine.HandleAsync("ret", ct), ct);
        try
        {
            while (!File.Exists(started))
            {
                await Task.Delay(5, ct);
            }

            var before = engine.Status;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var queued = rollback ? engine.RollbackAsync(mark, cancellation.Token) : engine.HandleAsync("ldc.i4.7", cancellation.Token);
            Assert.IsFalse(queued.IsCompleted);
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5), ct));
            Assert.IsFalse(running.IsCompleted, "The gate owner has not been released.");
            Assert.AreEqual(before, engine.Status);
        }
        finally
        {
            try
            {
                File.WriteAllText(release, "release");
                await running;
            }
            finally
            {
                File.Delete(started);
                File.Delete(release);
            }
        }

        Assert.AreEqual((await running).Status, engine.Status);
        Assert.IsTrue((await engine.HandleAsync(".clear", ct)).Succeeded);
        Assert.IsTrue((await engine.HandleAsync("ldc.i4.s 9", ct)).Succeeded);
    }
}
