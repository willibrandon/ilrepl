using IlRepl.Engine;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// Matches interruption to actual operation identities across real disposable session runtimes.
/// </summary>
[TestClass]
[TestCategory("Interaction")]
public sealed class SessionInterruptionTests
{
    /// <summary>
    /// Supplies cancellation for real host execution and filesystem synchronization.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// An interrupt delayed from a completed operation cannot cancel a later session replay in another runtime.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public Task StaleIdentity_DoesNotCancelNewReplay() => RunAsync(interruptCurrent: false);

    /// <summary>
    /// An interrupt matching the current replay stops its runtime while retaining the entire experiment.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public Task CurrentIdentity_CancelsReplayAndRetainsSource() => RunAsync(interruptCurrent: true);

    private async Task RunAsync(bool interruptCurrent)
    {
        var token = TestContext.CancellationToken;
        using var files = new SessionWorkspaceFixture();
        var release = Path.Combine(files.DirectoryPath, "release");
        string[] source =
        [
            "ldstr " + LiteralParser.Escape(files.MarkerPath), "ldstr \"entered\"",
            "call void File::WriteAllText(string, string)", "WAIT: ldstr " + LiteralParser.Escape(release),
            "call bool File::Exists(string)", "brfalse WAIT", "ldc.i4 42",
        ];
        await files.WriteAsync(new SessionDocument
        {
            Entries = [new SessionEntry { Source = source }, new SessionEntry { Kind = SessionEntryKind.Run, Source = ["ret"] }],
            Cells = [new SessionCell { Source = [.. source, "ret"] }],
        }, token);

        await using var controller = await SessionWorkspaceFixture.StartAsync(token);
        var opened = await controller.SessionAsync(new SessionRequest
        {
            Action = new SessionAction { Operation = SessionOperation.Open, Path = files.SessionPath },
        }, token);

        Assert.IsTrue(opened.Reply.Succeeded);
        var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(ExecutionProgress progress)
        {
            if (progress.IsRunning)
            {
                observed.TrySetResult(progress.Identity);
            }
        }

        controller.ProgressChanged += Observe;
        string previous;
        try
        {
            Assert.IsTrue((await controller.HandleAsync(".help", token)).Succeeded);
            previous = await observed.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
        }
        finally
        {
            controller.ProgressChanged -= Observe;
        }

        var replay = controller.SessionAsync(new SessionRequest { Action = new SessionAction { Operation = SessionOperation.Run } }, token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            while (!File.Exists(files.MarkerPath))
            {
                await Task.Delay(10, deadline.Token);
            }

            var active = controller.Progress;
            Assert.IsTrue(active.IsRunning);
            Assert.AreNotEqual(previous, active.Identity);
            Assert.AreEqual(interruptCurrent, await controller.InterruptAsync(interruptCurrent ? active.Identity : previous, token));
            await File.WriteAllTextAsync(release, "release", token);
            var result = await replay.WaitAsync(TimeSpan.FromSeconds(20), token);
            Assert.AreEqual(!interruptCurrent, result.Reply.Succeeded,
                string.Join('\n', result.Reply.Lines.Select(line => line.PlainText)));
            Assert.AreSequenceEqual([.. source, "ret"], Assert.ContainsSingle(result.Document.Cells).Source);
            if (interruptCurrent)
            {
                Assert.AreEqual(1, Assert.ContainsSingle(result.Document.Interruptions).Number);
                Assert.Contains(line => line.PlainText.Contains("session run cancelled", StringComparison.Ordinal), result.Reply.Lines);
            }
            else
            {
                Assert.IsEmpty(result.Document.Interruptions);
                Assert.Contains(line => line.PlainText.Contains("= 42 : int32", StringComparison.Ordinal), result.Reply.Lines);
            }
        }
        finally
        {
            await File.WriteAllTextAsync(release, "release", CancellationToken.None);
            await replay.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
        }
    }
}
