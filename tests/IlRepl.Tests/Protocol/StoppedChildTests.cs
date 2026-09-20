using System.Diagnostics;
using System.Runtime.InteropServices;
using IlRepl.Protocol;

namespace IlRepl.Tests.Protocol;

/// <summary>
/// A stopped child of this process does not keep ilrepl from telling whether a process runs.
/// </summary>
[TestClass]
public sealed partial class StoppedChildTests
{
    /// <summary>
    /// Supplies cancellation for the real child processes.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// The macOS runtime keeps its lock over all child processes while one of them is stopped, and the answer does not wait for it.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task IsRunning_AnswersWhileAChildOfThisProcessIsStopped()
    {
        var token = TestContext.CancellationToken;
        using var self = Process.GetCurrentProcess();
        var scope = OwnedProcessGroup.Describe(self, "self");
        using var stopped = Process.Start(new ProcessStartInfo("/bin/sleep") { ArgumentList = { "60" } })!;
        using var ended = Process.Start(new ProcessStartInfo("/bin/sleep") { ArgumentList = { "60" } })!;
        try
        {
            Assert.AreEqual(0, Signal(stopped.Id, OperatingSystem.IsMacOS() ? 17 : 19));

            // Only the runtime's SIGCHLD handler marks a child as ended. Once it has, it is past the point where macOS reports
            // the stopped child to it as ended too, and it asks about that child again and again with the lock held.
            Assert.AreEqual(0, Signal(ended.Id, 9));
            while (!ended.HasExited)
            {
                await Task.Delay(10, token);
            }

            var answer = Task.Run(() => OwnedProcessGroup.IsRunning(scope), token);

            Assert.IsTrue(await answer.WaitAsync(TimeSpan.FromSeconds(10), token));
        }
        finally
        {
            // A signal needs none of the runtime's locks, and the end of the stopped child is what releases them.
            _ = Signal(stopped.Id, 9);
            _ = Signal(ended.Id, 9);
            await stopped.WaitForExitAsync(CancellationToken.None);
            await ended.WaitForExitAsync(CancellationToken.None);
        }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Signal(int process, int signal);
}
